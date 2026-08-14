namespace EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;

/// <summary>Reports whether one success-exit terminal continuation crossed the first-bound-run completion boundary.</summary>
/// <param name="Disposition">The typed caller action required after this attempt.</param>
/// <param name="Status">The durable usage-ledger posture observed after the operation.</param>
/// <param name="CommitInvoked">Whether the caller's exact idempotent terminal continuation was invoked.</param>
/// <param name="Detail">A bounded diagnostic safe for runtime evidence.</param>
public sealed record GovernedLoopFirstBoundRunCompletionExecutionResult(
    GovernedLoopFirstBoundRunCompletionDisposition Disposition,
    GovernedLoopEffectAuthorityUsageStoreStatus Status,
    bool CommitInvoked,
    string Detail);
