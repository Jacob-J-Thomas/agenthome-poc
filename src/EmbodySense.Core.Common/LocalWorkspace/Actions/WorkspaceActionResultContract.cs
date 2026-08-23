using System.Buffers;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Common.LocalWorkspace.Actions;

/// <summary>Validates and canonically encodes the value-free schema-1 graph Action result.</summary>
public static class WorkspaceActionResultContract
{
    /// <summary>Creates and validates one exact result.</summary>
    public static WorkspaceActionResult Create(WorkspaceActionResultStatus status, string afterEvidenceId, long effectGeneration)
    {
        if (status is not (WorkspaceActionResultStatus.Committed or WorkspaceActionResultStatus.Replayed))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if (!WorkspaceActionFingerprint.IsEvidenceIdentifier(afterEvidenceId))
        {
            throw new ArgumentException("A bounded content-addressed after-evidence reference is required.", nameof(afterEvidenceId));
        }
        if (effectGeneration is < 1 or > Loops.Execution.GovernedLoopExecutionLimits.MaxVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(effectGeneration));
        }

        return new WorkspaceActionResult(WorkspaceActionResult.CurrentSchemaVersion, status, afterEvidenceId, effectGeneration);
    }

    /// <summary>Encodes one validated result as compact deterministic JSON.</summary>
    public static string Encode(WorkspaceActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var validated = Create(result.Status, result.AfterEvidenceId, result.EffectGeneration);
        if (result.SchemaVersion != WorkspaceActionResult.CurrentSchemaVersion)
        {
            throw new ArgumentException("The workspace action result schema is unsupported.", nameof(result));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("afterEvidenceId", validated.AfterEvidenceId);
            writer.WriteNumber("effectGeneration", validated.EffectGeneration);
            writer.WriteNumber("schemaVersion", validated.SchemaVersion);
            writer.WriteString("status", validated.Status == WorkspaceActionResultStatus.Committed ? "committed" : "replayed");
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Parses only the exact canonical schema-1 encoding.</summary>
    public static bool TryParse(string? canonicalJson, out WorkspaceActionResult? result)
    {
        result = null;
        if (string.IsNullOrEmpty(canonicalJson) || Encoding.UTF8.GetByteCount(canonicalJson) > WorkspaceActionContractLimits.MaxEvidenceUtf8Bytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(canonicalJson, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(new[] { "afterEvidenceId", "effectGeneration", "schemaVersion", "status" }, StringComparer.Ordinal) is false
                || root.GetProperty("schemaVersion").GetInt32() != WorkspaceActionResult.CurrentSchemaVersion
                || !root.GetProperty("effectGeneration").TryGetInt64(out var generation))
            {
                return false;
            }

            var status = root.GetProperty("status").GetString() switch
            {
                "committed" => WorkspaceActionResultStatus.Committed,
                "replayed" => WorkspaceActionResultStatus.Replayed,
                _ => WorkspaceActionResultStatus.Unknown,
            };
            var candidate = Create(status, root.GetProperty("afterEvidenceId").GetString()!, generation);
            if (!string.Equals(Encode(candidate), canonicalJson, StringComparison.Ordinal))
            {
                return false;
            }
            result = candidate;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return false;
        }
    }
}
