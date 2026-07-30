namespace EmbodySense.Core.Startup.Configuration.Models;

/// <summary>
/// Provides a display-oriented projection of one directory permission rule.
/// </summary>
/// <param name="Effect"><c>Approved</c> or <c>Denied</c>.</param>
/// <param name="Path">The configured rule path.</param>
/// <param name="Operations">Normalized lowercase operation names.</param>
/// <param name="RequiresApproval">Whether use requires human approval; denied rules project true.</param>
/// <param name="Detail">A human-readable explanation of the rule's effective behavior.</param>
public sealed record WorkspacePermissionRule(
    string Effect,
    string Path,
    IReadOnlyList<string> Operations,
    bool RequiresApproval,
    string Detail);
