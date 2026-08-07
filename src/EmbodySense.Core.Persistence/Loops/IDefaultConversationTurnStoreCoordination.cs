using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Coordinates deterministic observation of an active default-conversation turn-set operation.
/// </summary>
/// <remarks>
/// Lease-acquisition observation is invoked both after a safe handle is validated but before its exclusive OS lock and after that lock
/// but before final validation. Active-set and archival observations are invoked after the lease is acquired. Implementations must not
/// call back into the same store instance because the process gate and active-set lease are intentionally non-reentrant. Incomplete-stage
/// retirement is failure cleanup and its observation receives a non-cancelable token after the requested operation has already failed.
/// </remarks>
public interface IDefaultConversationTurnStoreCoordination
{
    /// <summary>
    /// Observes a validated active-set lease file at a bounded phase around the OS-exclusive lock.
    /// </summary>
    /// <param name="operation">The operation preparing to acquire the lease.</param>
    /// <param name="phase">The lease acquisition phase.</param>
    /// <param name="cancellationToken">Cancels the coordination wait.</param>
    /// <returns>A task that completes when lease acquisition may continue.</returns>
    Task ObserveActiveSetLeasePhaseAsync(
        DefaultConversationTurnStoreOperation operation,
        DefaultConversationTurnLeasePhase phase,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Observes an operation while it exclusively owns the active-turn-set coordination lease.
    /// </summary>
    /// <param name="operation">The operation that owns the active-turn-set lease.</param>
    /// <param name="cancellationToken">Cancels the coordination wait.</param>
    /// <returns>A task that completes when the operation may continue.</returns>
    Task BeforeActiveSetOperationAsync(DefaultConversationTurnStoreOperation operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Observes a phase of the identity-bound archival transition while the active-set lease remains held.
    /// </summary>
    /// <param name="operation">The operation preparing to archive the artifact.</param>
    /// <param name="turnId">The stable identity of the artifact being archived.</param>
    /// <param name="phase">The archival phase being observed.</param>
    /// <param name="cancellationToken">Cancels the coordination wait.</param>
    /// <returns>A task that completes when the archival transition may continue.</returns>
    Task ObserveArchivePhaseAsync(
        DefaultConversationTurnStoreOperation operation,
        string turnId,
        DefaultConversationTurnArchivePhase phase,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
