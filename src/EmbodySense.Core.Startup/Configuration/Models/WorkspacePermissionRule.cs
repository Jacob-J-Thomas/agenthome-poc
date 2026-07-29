namespace EmbodySense.Core.Startup.Configuration.Models;

public sealed record WorkspacePermissionRule(
    string Effect,
    string Path,
    IReadOnlyList<string> Operations,
    bool RequiresApproval,
    string Detail);
