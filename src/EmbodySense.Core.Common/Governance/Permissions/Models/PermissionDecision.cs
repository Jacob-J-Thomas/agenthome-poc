namespace EmbodySense.Core.Common.Governance.Permissions.Models;

/// <summary>
/// Identifies the supported permission decision values.
/// </summary>
public enum PermissionDecision
{
    /// <summary>
    /// Identifies the allow permission decision.
    /// </summary>
    Allow,
    /// <summary>
    /// Identifies the requires approval permission decision.
    /// </summary>
    RequiresApproval,
    /// <summary>
    /// Identifies the deny permission decision.
    /// </summary>
    Deny
}
