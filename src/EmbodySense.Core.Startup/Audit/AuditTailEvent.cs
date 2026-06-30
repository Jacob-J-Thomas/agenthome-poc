using System.Text.Json;
using EmbodySense.Core.Common.Governance.Audit.Models;

namespace EmbodySense.Core.Startup.Audit;

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
