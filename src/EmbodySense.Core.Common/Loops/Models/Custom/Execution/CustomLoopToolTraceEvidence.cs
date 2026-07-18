using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public sealed record CustomLoopToolTraceEvidence(
    CustomLoopToolEvidencePhase Phase,
    int RequestOrdinal,
    string RequestCorrelationId,
    string? BrokerRequestId,
    ToolCommand Command,
    string TargetPath,
    string? Content,
    string? Pattern,
    string? ResolvedTarget,
    CustomLoopToolAuthoritySnapshot Authority,
    ToolGovernanceEvidence? Governance,
    ToolExecutionOutcome? Outcome,
    string? CanonicalResultReturnedToModel,
    string? CanonicalResultHash,
    int? CanonicalResultCharacterCount,
    bool ReturnedToModel,
    int ReservedUtf8Bytes);
