using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Represents a custom loop tool trace evidence.
/// </summary>
/// <param name="Phase">The phase.</param>
/// <param name="RequestOrdinal">The request ordinal.</param>
/// <param name="RequestCorrelationId">The request correlation ID.</param>
/// <param name="BrokerRequestId">The broker request ID.</param>
/// <param name="Command">The command.</param>
/// <param name="TargetPath">The target path.</param>
/// <param name="Content">The exact content.</param>
/// <param name="Pattern">The pattern.</param>
/// <param name="ResolvedTarget">The resolved target.</param>
/// <param name="Authority">The authority.</param>
/// <param name="Governance">The governance.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="CanonicalResultReturnedToModel">The canonical result returned to model.</param>
/// <param name="CanonicalResultHash">The canonical result hash.</param>
/// <param name="CanonicalResultCharacterCount">The canonical result character count.</param>
/// <param name="ReturnedToModel">The returned to model.</param>
/// <param name="ReservedUtf8Bytes">The reserved UTF-8 bytes.</param>
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
