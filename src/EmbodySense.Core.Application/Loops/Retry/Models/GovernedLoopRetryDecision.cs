using EmbodySense.Core.Common.Loops.Execution.Retry.Models;

namespace EmbodySense.Core.Application.Loops.Retry.Models;

/// <summary>Returns a value-free deterministic retry decision and exact next-attempt identity.</summary>
/// <param name="Status">The closed decision status.</param>
/// <param name="Series">The authenticated immutable series identity when one can be proven.</param>
/// <param name="NextAttempt">The exact next attempt ordinal only when scheduling is admitted.</param>
/// <param name="EligibleAtUtc">The exact deterministic eligibility instant only when scheduling is admitted.</param>
/// <param name="AttemptOperationId">The deterministic next-attempt operation identity only when scheduling is admitted.</param>
/// <param name="Detail">A bounded value-free reason.</param>
public sealed record GovernedLoopRetryDecision(
    GovernedLoopRetryDecisionStatus Status,
    GovernedLoopRetrySeriesIdentity? Series,
    int? NextAttempt,
    DateTimeOffset? EligibleAtUtc,
    string? AttemptOperationId,
    string Detail);
