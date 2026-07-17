using EmbodySense.Core.Common.Governance.Permissions.Models;

namespace EmbodySense.Core.Common.Governance.Tools.Models;

public enum ToolAuthorityDecision
{
    Unknown = 0,
    Allowed = 1,
    Denied = 2
}

public enum ToolApprovalDecision
{
    Unknown = 0,
    NotEvaluated = 1,
    NotRequired = 2,
    Approved = 3,
    Rejected = 4,
    Requested = 5
}

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
