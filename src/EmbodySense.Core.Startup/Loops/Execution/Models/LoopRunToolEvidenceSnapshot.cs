namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Projects one bounded, correlated governed-tool request phase from the run trace.
/// </summary>
/// <param name="Phase">The phase.</param>
/// <param name="RequestOrdinal">The request ordinal.</param>
/// <param name="RequestCorrelationId">The request correlation identifier.</param>
/// <param name="BrokerRequestId">The broker request identifier.</param>
/// <param name="Command">The command.</param>
/// <param name="TargetPath">The target path.</param>
/// <param name="Content">The content.</param>
/// <param name="Pattern">The pattern.</param>
/// <param name="ResolvedTarget">The resolved target.</param>
/// <param name="Authority">The authority.</param>
/// <param name="Governance">The governance.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="CanonicalResultReturnedToModel">The canonical result returned to model.</param>
/// <param name="CanonicalResultHash">The canonical result hash.</param>
/// <param name="CanonicalResultCharacterCount">The canonical result character count.</param>
/// <param name="ReturnedToModel">The returned to model.</param>
/// <param name="ReservedUtf8Bytes">The reserved utf8 bytes.</param>
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
