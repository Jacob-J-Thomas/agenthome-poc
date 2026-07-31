using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Startup.Audit;
using System.Text.Json;

namespace EmbodySense.Core.Startup.Audit;

/// <summary>
/// Provides a display-safe projection of one canonical audit event.
/// </summary>
/// <param name="TimestampUtc">The UTC timestamp recorded by the audit producer.</param>
/// <param name="Action">The canonical action identifier.</param>
/// <param name="Target">The resource or surface targeted by the action.</param>
/// <param name="Outcome">The canonical outcome identifier.</param>
/// <param name="Detail">The human-readable event detail.</param>
/// <param name="Metadata">
/// String projections of structured metadata. String, numeric, boolean, null, object, and array JSON
/// values preserve their semantic text; null is represented as an empty string.
/// </param>
public sealed record AuditTailEvent(
    DateTimeOffset TimestampUtc,
    string Action,
    string Target,
    string Outcome,
    string Detail,
    IReadOnlyDictionary<string, string> Metadata)
{
    internal static AuditTailEvent FromAuditEvent(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        return new AuditTailEvent(
            auditEvent.TimestampUtc,
            auditEvent.Action,
            auditEvent.Target,
            auditEvent.Outcome,
            auditEvent.Detail,
            auditEvent.Metadata.ToDictionary(item => item.Key, item => FormatMetadataValue(item.Value), StringComparer.Ordinal));
    }

    private static string FormatMetadataValue(object? value)
    {
        return value switch
        {
            null => "",
            JsonElement element => FormatJsonElement(element),
            _ => value.ToString() ?? ""
        };
    }

    private static string FormatJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => element.GetRawText()
        };
    }
}
