using EmbodySense.Core.Common.Governance.Permissions.Models;

namespace EmbodySense.Core.Common.Governance.Tools.Models;

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
