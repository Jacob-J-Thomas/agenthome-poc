using System.Buffers;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Clients.Capabilities;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Secrets.Redaction;
using EmbodySense.Core.Common.Secrets.Redaction.Models;

namespace EmbodySense.Core.Clients.CommandActions;

internal static class CommandActionJsonOutputRedactor
{
    public static TextRedactionResult Redact(string canonicalJson, SensitiveRedactionScope? scope, RedactionSummary fallback)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        ArgumentNullException.ThrowIfNull(fallback);
        if (scope is null)
        {
            return Marker(SensitiveRedactionScope.ScopeLimitMarker, fallback);
        }

        using var document = JsonDocument.Parse(canonicalJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        var buffer = new ArrayBufferWriter<byte>();
        var summary = new RedactionSummary(RedactionStatus.Completed, scope.SensitiveValueCount, scope.IgnoredValueCount, 0, 0, 0);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (!TryWrite(writer, document.RootElement, scope, ref summary, out var marker))
            {
                return Marker(marker, summary);
            }
            writer.Flush();
        }

        var projection = Encoding.UTF8.GetString(buffer.WrittenSpan);
        if (!GovernedActuatorInputContract.TryCanonicalize(projection, out var canonical, out _)
            || canonical!.CanonicalJson.Length > CommandActionContractLimits.MaxRetainedOutputCharacters)
        {
            return Marker(SensitiveRedactionScope.OutputLimitMarker, WithStatus(summary, RedactionStatus.OutputLimitExceeded));
        }

        var residual = scope.RedactText(canonical.CanonicalJson);
        summary = Combine(summary, residual.Summary);
        return residual.Summary.Status == RedactionStatus.Completed && residual.Summary.ReplacementCount == 0
            ? new TextRedactionResult(canonical.CanonicalJson, summary)
            : Marker(residual.Value, summary);
    }

    private static bool TryWrite(
        Utf8JsonWriter writer,
        JsonElement value,
        SensitiveRedactionScope scope,
        ref RedactionSummary summary,
        out string marker)
    {
        marker = SensitiveRedactionScope.ProjectionSafetyMarker;
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = new List<(string Name, JsonElement Value)>();
                foreach (var property in value.EnumerateObject())
                {
                    if (!TrySanitize(property.Name, scope, ref summary, out var name, out marker))
                    {
                        return false;
                    }
                    properties.Add((name!, property.Value));
                }
                if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Count)
                {
                    summary = WithStatus(summary, RedactionStatus.ProjectionSafetyFailed);
                    return false;
                }
                writer.WriteStartObject();
                foreach (var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    if (!TryWrite(writer, property.Value, scope, ref summary, out marker))
                    {
                        return false;
                    }
                }
                writer.WriteEndObject();
                return true;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    if (!TryWrite(writer, item, scope, ref summary, out marker))
                    {
                        return false;
                    }
                }
                writer.WriteEndArray();
                return true;
            case JsonValueKind.String:
                if (!TrySanitize(value.GetString() ?? string.Empty, scope, ref summary, out var text, out marker))
                {
                    return false;
                }
                writer.WriteStringValue(text);
                return true;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                return true;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return true;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return true;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                return true;
            default:
                return false;
        }
    }

    private static bool TrySanitize(
        string value,
        SensitiveRedactionScope scope,
        ref RedactionSummary summary,
        out string? sanitized,
        out string marker)
    {
        var redacted = scope.RedactText(value);
        summary = Combine(summary, redacted.Summary);
        marker = redacted.Value;
        if (redacted.Summary.Status != RedactionStatus.Completed)
        {
            sanitized = null;
            return false;
        }
        sanitized = CommandActionEvidenceContract.SanitizeRetainedText(
            CapabilityProcessDiagnosticRedactor.Redact(redacted.Value, CommandActionContractLimits.MaxRetainedOutputCharacters));
        return true;
    }

    private static TextRedactionResult Marker(string marker, RedactionSummary summary)
    {
        var value = string.IsNullOrEmpty(marker) ? SensitiveRedactionScope.ScopeLimitMarker : marker;
        return new TextRedactionResult(JsonSerializer.Serialize(value), summary);
    }

    private static RedactionSummary WithStatus(RedactionSummary summary, RedactionStatus status)
        => new(status, summary.SensitiveValueCount, summary.IgnoredValueCount, summary.ReplacementCount, summary.ExaminedCharacterCount, summary.WorkUnitCount);

    private static RedactionSummary Combine(RedactionSummary first, RedactionSummary second)
        => new(
            first.Status == RedactionStatus.Completed ? second.Status : first.Status,
            Math.Max(first.SensitiveValueCount, second.SensitiveValueCount),
            Math.Max(first.IgnoredValueCount, second.IgnoredValueCount),
            checked(first.ReplacementCount + second.ReplacementCount),
            checked(first.ExaminedCharacterCount + second.ExaminedCharacterCount),
            checked(first.WorkUnitCount + second.WorkUnitCount));
}
