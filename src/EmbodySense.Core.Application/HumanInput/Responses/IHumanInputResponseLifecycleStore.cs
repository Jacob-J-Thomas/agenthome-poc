using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses;

/// <summary>Persists immutable Human Input responses, selections, and append-only response-operation evidence.</summary>
public interface IHumanInputResponseLifecycleStore
{
    /// <summary>Reads response state for one exact immutable request version.</summary>
    /// <param name="request">The exact immutable request reference.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The bounded current response snapshot or a fail-closed read result.</returns>
    Task<HumanInputResponseLifecycleStoreReadResult> ReadAsync(HumanInputRequestReference request, CancellationToken cancellationToken = default);

    /// <summary>Atomically reads request state, response state, and a workspace-global operation binding for mutation planning.</summary>
    /// <param name="requestId">The exact request lifecycle identity.</param>
    /// <param name="operationId">The workspace-global operation identity.</param>
    /// <param name="commandHash">The canonical exact-intent hash.</param>
    /// <param name="cancellationToken">A token that cancels work before durable intent.</param>
    /// <returns>The exact optimistic observation.</returns>
    Task<HumanInputResponseLifecycleStoreReadResult> ReadForMutationAsync(string requestId, string operationId, string commandHash, CancellationToken cancellationToken = default);

    /// <summary>Atomically appends response evidence and optional immutable response/selection artifacts and answered request head.</summary>
    /// <param name="mutation">The bounded optimistic append request.</param>
    /// <param name="cancellationToken">A token used only until durable intent begins.</param>
    /// <returns>The exact durable or fail-closed outcome.</returns>
    Task<HumanInputResponseLifecycleStoreCommitResult> CommitAsync(HumanInputResponseLifecycleStoreMutation mutation, CancellationToken cancellationToken = default);
}
