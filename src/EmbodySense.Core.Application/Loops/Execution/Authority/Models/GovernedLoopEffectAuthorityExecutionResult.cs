using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Authority.Models;

/// <summary>Returns one bounded authority decision and whether its protected continuation crossed the boundary.</summary>
/// <typeparam name="TResult">The protected continuation result type.</typeparam>
/// <param name="Status">The closed application execution posture.</param>
/// <param name="Decision">The validated immutable decision when one could be constructed.</param>
/// <param name="EvidenceStatus">The exact append-only evidence-store outcome.</param>
/// <param name="CommitInvoked">Whether the protected continuation was invoked exactly once.</param>
/// <param name="Result">The continuation result when it was invoked.</param>
/// <param name="Detail">A bounded operator-safe explanation.</param>
public sealed record GovernedLoopEffectAuthorityExecutionResult<TResult>(
    GovernedLoopEffectAuthorityExecutionStatus Status,
    GovernedLoopEffectAuthorityDecision? Decision,
    GovernedLoopEffectAuthorityEvidenceStoreStatus EvidenceStatus,
    bool CommitInvoked,
    TResult? Result,
    string Detail);
