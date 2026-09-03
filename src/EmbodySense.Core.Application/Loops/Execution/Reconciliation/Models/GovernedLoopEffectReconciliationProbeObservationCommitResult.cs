using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Returns the durable observation commit disposition and exact unchanged effect head.</summary>
/// <param name="Status">The closed commit disposition.</param>
/// <param name="Case">The exact resulting case when an observation was appended.</param>
/// <param name="EffectHead">The exact original effect head; probes never create an effect successor.</param>
public sealed record GovernedLoopEffectReconciliationProbeObservationCommitResult(
    GovernedLoopEffectReconciliationProbeReservationStatus Status,
    GovernedLoopEffectReconciliationCase? Case,
    GovernedLoopEffectAttempt? EffectHead)
{
    /// <summary>Gets the closed commit disposition.</summary>
    public GovernedLoopEffectReconciliationProbeReservationStatus Status { get; } = GovernedLoopEffectReconciliationModelGuard.RequireDefinedStatus(Status, nameof(Status));
    /// <summary>Gets the exact resulting case when available.</summary>
    public GovernedLoopEffectReconciliationCase? Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyProbeCommitCase(Status, Case, EffectHead, nameof(Case));
    /// <summary>Gets the exact unchanged original effect head.</summary>
    public GovernedLoopEffectAttempt? EffectHead { get; } = GovernedLoopEffectReconciliationModelGuard.CopyProbeCommitEffect(Status, Case, EffectHead, nameof(EffectHead));
}
