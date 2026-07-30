using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Permissions;

namespace EmbodySense.Core.Common.Governance.Permissions;

/// <summary>
/// Records the decision, most-specific matched path, and explanation produced by permission evaluation.
/// </summary>
/// <param name="Decision">The resulting allow, approval-required, or deny decision.</param>
/// <param name="MatchedPath">The normalized path of the rule that determined the result.</param>
/// <param name="Detail">The human-readable evaluation evidence.</param>
public sealed record PermissionEvaluation(PermissionDecision Decision, string MatchedPath, string Detail)
{
    /// <summary>
    /// Creates an allow decision that requires no additional human approval.
    /// </summary>
    /// <param name="matchedPath">The matched path.</param>
    /// <returns>The permission evaluation.</returns>
    public static PermissionEvaluation Allowed(string matchedPath) => new(PermissionDecision.Allow, matchedPath, PermissionEvaluationDetails.ApprovedWithoutAdditionalHumanApproval);

    /// <summary>
    /// Creates a decision that must receive human approval before execution.
    /// </summary>
    /// <param name="matchedPath">The matched path.</param>
    /// <param name="detail">The detail.</param>
    /// <returns>The permission evaluation.</returns>
    public static PermissionEvaluation RequiresApproval(string matchedPath, string detail) => new(PermissionDecision.RequiresApproval, matchedPath, detail);

    /// <summary>
    /// Creates a deny decision.
    /// </summary>
    /// <param name="matchedPath">The matched path.</param>
    /// <param name="detail">The detail.</param>
    /// <returns>The permission evaluation.</returns>
    public static PermissionEvaluation Denied(string matchedPath, string detail) => new(PermissionDecision.Deny, matchedPath, detail);
}
