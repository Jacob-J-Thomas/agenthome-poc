namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunToolEvidenceSnapshot(
    string Phase,
    int RequestOrdinal,
    string RequestCorrelationId,
    string? BrokerRequestId,
    string Command,
    string TargetPath,
    string? Content,
    string? Pattern,
    string? ResolvedTarget,
    LoopRunToolAuthoritySnapshot Authority,
    LoopRunToolGovernanceSnapshot? Governance,
    string? Outcome,
    string? CanonicalResultReturnedToModel,
    string? CanonicalResultHash,
    int? CanonicalResultCharacterCount,
    bool ReturnedToModel,
    int ReservedUtf8Bytes);
