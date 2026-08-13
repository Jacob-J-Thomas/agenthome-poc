using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Captures one bounded current coordinator evidence head without loading an unbounded failure history.</summary>
/// <param name="Ownership">The current fenced ownership claim.</param>
/// <param name="LatestLifecycle">The latest lifecycle state for that ownership.</param>
/// <param name="LatestHeartbeat">The latest exclusive-lease heartbeat for that ownership.</param>
/// <param name="LatestFailureSequence">The latest failure sequence, or zero when no failure exists for this ownership.</param>
/// <param name="LatestFailureHash">The latest failure hash, present exactly when the sequence is positive.</param>
public sealed record GovernedLoopCoordinatorSnapshot(
    GovernedLoopCoordinatorOwnership Ownership,
    GovernedLoopCoordinatorLifecycle LatestLifecycle,
    GovernedLoopCoordinatorHeartbeat LatestHeartbeat,
    long LatestFailureSequence,
    string? LatestFailureHash)
{
    /// <summary>Gets a detached copy of the current ownership evidence.</summary>
    public GovernedLoopCoordinatorOwnership Ownership { get; } = GovernedLoopCoordinatorEvidenceCopy.Ownership(Ownership);

    /// <summary>Gets a detached copy of the latest lifecycle evidence.</summary>
    public GovernedLoopCoordinatorLifecycle LatestLifecycle { get; } = GovernedLoopCoordinatorEvidenceCopy.Lifecycle(LatestLifecycle);

    /// <summary>Gets a detached copy of the latest heartbeat evidence.</summary>
    public GovernedLoopCoordinatorHeartbeat LatestHeartbeat { get; } = GovernedLoopCoordinatorEvidenceCopy.Heartbeat(LatestHeartbeat);
}
