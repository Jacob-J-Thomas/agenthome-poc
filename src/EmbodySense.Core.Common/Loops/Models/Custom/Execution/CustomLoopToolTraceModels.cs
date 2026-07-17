using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public sealed record CustomLoopToolAuthoritySnapshot(
    string RoleId,
    CustomLoopToolAssignment[] AdmittedMaximum,
    CustomLoopToolAssignment[] CurrentRoleCeiling,
    CustomLoopToolAssignment[] ImplementedCatalog,
    CustomLoopToolAssignment[] EffectiveAssignments,
    string RoleCeilingHash,
    string CatalogHash,
    DateTimeOffset EvaluatedAtUtc,
    bool IsValid,
    string Detail);

public enum CustomLoopToolEvidencePhase
{
    Unknown = 0,
    RequestReserved = 1,
    GovernanceDecided = 2,
    OutcomeObserved = 3,
    IntegrityFailed = 4
}

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
