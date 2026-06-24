namespace EmbodySense.Core.Startup.Configuration;

public sealed record WorkspacePermissionRule(
    string Effect,
    string Path,
    IReadOnlyList<string> Operations,
    bool RequiresApproval,
    string Detail);
