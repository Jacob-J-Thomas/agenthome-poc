namespace EmbodySense.Core.Startup.Configuration;

public sealed record WorkspaceAuditConfiguration(
    string Path,
    bool Exists,
    IReadOnlyList<WorkspaceAuditLogEvent> Events,
    IReadOnlyList<string> ReadProblems);
