using EmbodySense.Core.Common.Governance.Permissions.Models;

namespace EmbodySense.Core.Common.Governance.Tools.Models;

/// <summary>
/// Represents a tool governance evidence.
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
public sealed record ToolGovernanceEvidence(
    ToolAuthorityDecision AuthorityDecision,
    string AuthorityDetail,
    PermissionDecision? PermissionDecision,
    string? PermissionMatchedPath,
    string? PermissionDetail,
    string? PermissionPolicyHash,
    ToolApprovalDecision ApprovalDecision,
    string? ApprovalDecisionBy,
    string? ApprovalDetail);
