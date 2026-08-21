using System.Buffers;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.CommandActions.Models;

namespace EmbodySense.Core.Common.CommandActions;

/// <summary>Validates and canonically encodes value-free schema-1 graph command Action results.</summary>
public static class CommandActionResultContract
{
    /// <summary>Creates one exact bounded command result.</summary>
    public static CommandActionResult Create(
        CommandActionResultStatus status,
        CommandActionResultOutcome outcome,
        string outcomeEvidenceId,
        long effectGeneration)
    {
        if (status is not (CommandActionResultStatus.Committed or CommandActionResultStatus.Replayed))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if (outcome is not (CommandActionResultOutcome.Succeeded or CommandActionResultOutcome.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
        if (!CommandActionFingerprint.IsEvidenceIdentifier(outcomeEvidenceId))
        {
            throw new ArgumentException("A bounded command outcome-evidence reference is required.", nameof(outcomeEvidenceId));
        }
        if (effectGeneration is < 1 or > Loops.Execution.GovernedLoopExecutionLimits.MaxVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(effectGeneration));
        }
        return new CommandActionResult(CommandActionResult.CurrentSchemaVersion, status, outcome, outcomeEvidenceId, effectGeneration);
    }

    /// <summary>Encodes one validated result as compact deterministic JSON.</summary>
    public static string Encode(CommandActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var validated = Create(result.Status, result.Outcome, result.OutcomeEvidenceId, result.EffectGeneration);
        if (result.SchemaVersion != CommandActionResult.CurrentSchemaVersion)
        {
            throw new ArgumentException("The command Action result schema is unsupported.", nameof(result));
        }
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("effectGeneration", validated.EffectGeneration);
            writer.WriteString("outcome", validated.Outcome == CommandActionResultOutcome.Succeeded ? "succeeded" : "failed");
            writer.WriteString("outcomeEvidenceId", validated.OutcomeEvidenceId);
            writer.WriteNumber("schemaVersion", validated.SchemaVersion);
            writer.WriteString("status", validated.Status == CommandActionResultStatus.Committed ? "committed" : "replayed");
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Parses only the exact canonical schema-1 encoding.</summary>
    public static bool TryParse(string? canonicalJson, out CommandActionResult? result)
    {
        result = null;
        if (string.IsNullOrEmpty(canonicalJson) || Encoding.UTF8.GetByteCount(canonicalJson) > CommandActionContractLimits.MaxEvidenceUtf8Bytes)
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(canonicalJson, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(
                    ["effectGeneration", "outcome", "outcomeEvidenceId", "schemaVersion", "status"],
                    StringComparer.Ordinal)
                || root.GetProperty("schemaVersion").GetInt32() != CommandActionResult.CurrentSchemaVersion
                || !root.GetProperty("effectGeneration").TryGetInt64(out var generation))
            {
                return false;
            }
            var status = root.GetProperty("status").GetString() switch
            {
                "committed" => CommandActionResultStatus.Committed,
                "replayed" => CommandActionResultStatus.Replayed,
                _ => CommandActionResultStatus.Unknown,
            };
            var outcome = root.GetProperty("outcome").GetString() switch
            {
                "succeeded" => CommandActionResultOutcome.Succeeded,
                "failed" => CommandActionResultOutcome.Failed,
                _ => CommandActionResultOutcome.Unknown,
            };
            var candidate = Create(status, outcome, root.GetProperty("outcomeEvidenceId").GetString()!, generation);
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
