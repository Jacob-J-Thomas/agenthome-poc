namespace EmbodySense.Core.Startup.Workspace;

public sealed record WorkspaceStatusSnapshot(
    string RootPath,
    string AgentPath,
    string WorkspacePath,
    bool IsInitialized,
    string EventsLogPath,
    string PermissionsPath,
    string TasksPath,
    string DefaultAccess,
    IReadOnlyList<string> ApprovedEntries,
    IReadOnlyList<string> DeniedEntries);
