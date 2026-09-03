using EmbodySense.Core.Application.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Commits the one terminal probe callback result against its already durable reservation.</summary>
/// <param name="Reservation">The exact durable reservation returned before callback.</param>
/// <param name="Result">The callback result, including an observation only when ready.</param>
public sealed record GovernedLoopEffectReconciliationProbeObservationCommitRequest(
    GovernedLoopEffectReconciliationProbeReservation Reservation,
    GovernedLoopEffectReconciliationProbeInvocationResult Result)
{
    /// <summary>Gets the detached exact reservation.</summary>
    public GovernedLoopEffectReconciliationProbeReservation Reservation { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredReservation(Reservation, nameof(Reservation));
    /// <summary>Gets the detached terminal callback result.</summary>
    public GovernedLoopEffectReconciliationProbeInvocationResult Result { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredProbeResult(Result, nameof(Result));
}
