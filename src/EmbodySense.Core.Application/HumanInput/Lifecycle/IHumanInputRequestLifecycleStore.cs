using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle;

/// <summary>Persists authenticated Human Input request versions, lifecycle heads, and workspace-global operation evidence.</summary>
public interface IHumanInputRequestLifecycleStore
{
    /// <summary>Reads one exact request lifecycle without following supersession links.</summary>
    /// <param name="requestId">The stable exact request identity.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The current bounded snapshot or a fail-closed read result.</returns>
    Task<HumanInputRequestLifecycleStoreReadResult> ReadAsync(string requestId, CancellationToken cancellationToken = default);

    /// <summary>Atomically reads one target lifecycle, an optional exact related lifecycle, and a workspace-global operation binding for mutation planning.</summary>
    /// <param name="requestId">The stable exact target request identity.</param>
    /// <param name="operationId">The workspace-global operation identity.</param>
    /// <param name="requestHash">The canonical exact-intent hash.</param>
    /// <param name="relatedRequestId">The exact related request identity to observe atomically, or null when the operation has no caller-supplied relation.</param>
    /// <param name="cancellationToken">A token that cancels work before durable intent.</param>
    /// <returns>The exact optimistic observation.</returns>
    Task<HumanInputRequestLifecycleStoreReadResult> ReadForMutationAsync(
        string requestId,
        string operationId,
        string requestHash,
        string? relatedRequestId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically appends immutable request intent, writes one or two lifecycle heads, and records terminal operation evidence.</summary>
    /// <param name="mutation">The bounded optimistic append request.</param>
    /// <param name="cancellationToken">A token used only until durable intent begins.</param>
    /// <returns>The exact durable or fail-closed outcome.</returns>
    Task<HumanInputRequestLifecycleStoreCommitResult> CommitAsync(HumanInputRequestLifecycleStoreMutation mutation, CancellationToken cancellationToken = default);
}
