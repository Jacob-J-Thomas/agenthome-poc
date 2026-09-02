using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Returns the detached durable reservation state for a probe operation.</summary>
/// <param name="Status">The closed reservation disposition.</param>
/// <param name="Reservation">The exact reservation when safely established.</param>
/// <param name="Case">The exact completed case when replay found a committed observation.</param>
/// <param name="EffectHead">The unchanged exact effect head when replay found a committed observation.</param>
public sealed record GovernedLoopEffectReconciliationProbeReservationResult(
    GovernedLoopEffectReconciliationProbeReservationStatus Status,
    GovernedLoopEffectReconciliationProbeReservation? Reservation,
    GovernedLoopEffectReconciliationCase? Case = null,
    GovernedLoopEffectAttempt? EffectHead = null)
{
    /// <summary>Gets the closed reservation disposition.</summary>
    public GovernedLoopEffectReconciliationProbeReservationStatus Status { get; } = GovernedLoopEffectReconciliationModelGuard.RequireDefinedStatus(Status, nameof(Status));
    /// <summary>Gets the exact reservation only for reserved or replayed outcomes.</summary>
    public GovernedLoopEffectReconciliationProbeReservation? Reservation { get; } = GovernedLoopEffectReconciliationModelGuard.CopyReservationPayload(Status, Reservation, nameof(Reservation));

    /// <summary>Gets the exact completed case only when replay safely includes the observation commit.</summary>
    public GovernedLoopEffectReconciliationCase? Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyProbeReplayCase(Status, Case, EffectHead, nameof(Case));

    /// <summary>Gets the exact unchanged effect head only when replay safely includes the observation commit.</summary>
    public GovernedLoopEffectAttempt? EffectHead { get; } = GovernedLoopEffectReconciliationModelGuard.CopyProbeReplayEffect(Status, Case, EffectHead, nameof(EffectHead));
}
