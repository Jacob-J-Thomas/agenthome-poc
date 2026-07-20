namespace EmbodySense.Core.Startup.Loops.Execution;

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
