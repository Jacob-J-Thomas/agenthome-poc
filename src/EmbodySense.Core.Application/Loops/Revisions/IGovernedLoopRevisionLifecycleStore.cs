using EmbodySense.Core.Application.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions;

/// <summary>Atomically persists immutable governed-loop revision artifacts, lifecycle heads, and append-only operation evidence.</summary>
public interface IGovernedLoopRevisionLifecycleStore
{
    /// <summary>Reads one exact graph aggregate without selecting a current revision on the caller's behalf.</summary>
    /// <param name="graphId">The exact graph identifier.</param>
    /// <param name="cancellationToken">The cancellation token used while reading.</param>
    /// <returns>The global store generation and exact graph snapshot when safely available.</returns>
    Task<GovernedLoopRevisionGraphReadResult> ReadGraphAsync(
        string graphId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one graph aggregate and any workspace-global receipt already bound to the operation identifier.</summary>
    /// <param name="graphId">The exact graph identifier being mutated.</param>
    /// <param name="operationId">The workspace-global operation identifier being checked for replay or changed intent.</param>
    /// <param name="requestHash">The canonical request hash required to recover only an exact pending intent.</param>
    /// <param name="cancellationToken">The cancellation token used before durable work begins.</param>
    /// <returns>The global store generation, graph snapshot, and matching operation binding when available.</returns>
    Task<GovernedLoopRevisionStoreReadResult> ReadForMutationAsync(
        string graphId,
        string operationId,
        string requestHash,
        CancellationToken cancellationToken = default);

    /// <summary>Commits one pre-authorized, pre-validated mutation against an exact global store generation.</summary>
    /// <remarks>The store enforces atomic generation and operation-id uniqueness only; it does not infer authority, admission, or lifecycle policy.</remarks>
    /// <param name="mutation">The exact application-planned artifact, head, and terminal evidence.</param>
    /// <param name="cancellationToken">The cancellation token used before durable intent publication.</param>
    /// <returns>The exact durable outcome, evidence, and current graph snapshot when safely available.</returns>
    Task<GovernedLoopRevisionStoreCommitResult> CommitAsync(
        GovernedLoopRevisionStoreMutation mutation,
        CancellationToken cancellationToken = default);
}
