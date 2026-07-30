namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Projects authority, permission, and approval decisions retained before tool actuation.
/// </summary>
/// <param name="AuthorityDecision">The authority decision.</param>
/// <param name="AuthorityDetail">The authority detail.</param>
/// <param name="PermissionDecision">The permission decision.</param>
/// <param name="PermissionMatchedPath">The permission matched path.</param>
/// <param name="PermissionDetail">The permission detail.</param>
/// <param name="PermissionPolicyHash">The permission policy hash.</param>
/// <param name="ApprovalDecision">The approval decision.</param>
/// <param name="ApprovalDecisionBy">The approval decision by.</param>
/// <param name="ApprovalDetail">The approval detail.</param>
public sealed record LoopRunToolGovernanceSnapshot(
    string AuthorityDecision,
    string AuthorityDetail,
    string? PermissionDecision,
    string? PermissionMatchedPath,
    string? PermissionDetail,
    string? PermissionPolicyHash,
    string ApprovalDecision,
    string? ApprovalDecisionBy,
    string? ApprovalDetail);
