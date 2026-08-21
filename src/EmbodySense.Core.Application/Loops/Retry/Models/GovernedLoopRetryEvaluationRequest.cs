using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using EmbodySense.Core.Common.Loops.Failures.Models;

namespace EmbodySense.Core.Application.Loops.Retry.Models;

/// <summary>Requests deterministic evaluation of one exact failed node attempt under fresh current posture.</summary>
/// <param name="Policy">The immutable opt-in policy bound into the admitted graph revision.</param>
/// <param name="Failure">The exact durable classified failure.</param>
/// <param name="ExistingSeries">The retained series identity after the first failed attempt, or null for the first evaluation.</param>
/// <param name="CurrentAttempt">The exact positive attempt that just failed.</param>
/// <param name="Budget">The authoritative or conservative cumulative budget snapshot after that attempt.</param>
/// <param name="SeriesStartedAtUtc">The trusted UTC start of the first attempt in this series.</param>
/// <param name="EvaluatedAtUtc">The fresh trusted UTC evaluation instant.</param>
/// <param name="EnclosingDeadlineUtc">The optional immutable enclosing run or cycle deadline.</param>
/// <param name="CurrentLifecycleEligible">Whether the run remains active and neither paused nor cancelled.</param>
/// <param name="CurrentAuthorityEligible">Whether the exact current authority still admits the node.</param>
/// <param name="CurrentDependenciesEligible">Whether exact current capability and provider dependencies remain admitted.</param>
public sealed record GovernedLoopRetryEvaluationRequest(
    GovernedLoopRetryPolicy Policy,
    GovernedLoopFailureEvidence Failure,
    GovernedLoopRetrySeriesIdentity? ExistingSeries,
    int CurrentAttempt,
    GovernedLoopRetryBudgetSnapshot Budget,
    DateTimeOffset SeriesStartedAtUtc,
    DateTimeOffset EvaluatedAtUtc,
    DateTimeOffset? EnclosingDeadlineUtc,
    bool CurrentLifecycleEligible,
    bool CurrentAuthorityEligible,
    bool CurrentDependenciesEligible)
{
    /// <summary>Gets the explicit current lifecycle reason; the legacy eligibility bit maps to Active or Inactive by default.</summary>
    public GovernedLoopRetryLifecyclePosture LifecyclePosture { get; init; } = CurrentLifecycleEligible
        ? GovernedLoopRetryLifecyclePosture.Active
        : GovernedLoopRetryLifecyclePosture.Inactive;
}
