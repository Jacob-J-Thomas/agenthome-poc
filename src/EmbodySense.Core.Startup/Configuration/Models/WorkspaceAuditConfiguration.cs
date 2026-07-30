namespace EmbodySense.Core.Startup.Configuration.Models;

public sealed record WorkspaceAuditConfiguration(
    string Path,
    bool Exists,
    IReadOnlyList<WorkspaceAuditLogEvent> Events,
    IReadOnlyList<string> ReadProblems);
