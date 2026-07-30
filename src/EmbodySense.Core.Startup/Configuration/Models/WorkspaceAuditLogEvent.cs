namespace EmbodySense.Core.Startup.Configuration.Models;

public sealed record WorkspaceAuditLogEvent(
    int Sequence,
    DateTimeOffset TimestampUtc,
    string Actor,
    string Action,
    string Target,
    string Outcome,
    string Detail,
    IReadOnlyDictionary<string, string> Metadata);
