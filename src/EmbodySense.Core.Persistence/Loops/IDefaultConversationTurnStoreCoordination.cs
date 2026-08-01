namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Coordinates deterministic observation of an active default-conversation turn-set operation.
/// </summary>
/// <remarks>
/// Implementations are invoked only after the workspace active-set lease is acquired and before the operation reads or mutates artifacts.
/// They must not call back into the same store instance because the active-set lease is intentionally non-reentrant.
/// </remarks>
public interface IDefaultConversationTurnStoreCoordination
{
    /// <summary>
    /// Observes an operation while it exclusively owns the active-turn-set coordination lease.
    /// </summary>
    /// <param name="operation">The operation that owns the active-turn-set lease.</param>
    /// <param name="cancellationToken">Cancels the coordination wait.</param>
    /// <returns>A task that completes when the operation may continue.</returns>
    Task BeforeActiveSetOperationAsync(DefaultConversationTurnStoreOperation operation, CancellationToken cancellationToken = default);
}
