namespace EmbodySense.Core.Startup.Configuration;

public sealed record WorkspacePermissionsConfiguration(
    string Path,
    bool Exists,
    bool Parsed,
    int? Version,
    string Scope,
    string DefaultAccess,
    string RawJson,
    IReadOnlyList<WorkspacePermissionRule> Approved,
    IReadOnlyList<WorkspacePermissionRule> Denied,
    IReadOnlyList<string> ReadProblems);
