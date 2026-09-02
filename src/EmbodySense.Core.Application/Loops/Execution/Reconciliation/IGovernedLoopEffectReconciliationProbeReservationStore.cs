using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

/// <summary>Owns the durable reservation and observation commit boundary for registered reconciliation probes.</summary>
/// <remarks>The implementation uses the canonical effect-attempt root and mutation lease; reservation occurs before any callback.</remarks>
public interface IGovernedLoopEffectReconciliationProbeReservationStore
{
    /// <summary>Reserves one independent probe operation before its callback may be entered.</summary>
    /// <param name="request">The exact immutable callback context and complete intent hash.</param>
    /// <param name="cancellationToken">Cancels before durable reservation begins.</param>
    /// <returns>The winner reservation, replay, conflict, or closed persistence status.</returns>
    Task<GovernedLoopEffectReconciliationProbeReservationResult> ReserveAsync(GovernedLoopEffectReconciliationProbeReservationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Commits the one terminal callback observation and optional case successor without changing the effect head.</summary>
    /// <param name="request">The exact prior reservation and callback result.</param>
    /// <param name="cancellationToken">Cancels before durable observation commit begins.</param>
    /// <returns>The exact resulting case and unchanged effect head or a closed persistence status.</returns>
    Task<GovernedLoopEffectReconciliationProbeObservationCommitResult> CommitObservationAsync(GovernedLoopEffectReconciliationProbeObservationCommitRequest request, CancellationToken cancellationToken = default);
}
