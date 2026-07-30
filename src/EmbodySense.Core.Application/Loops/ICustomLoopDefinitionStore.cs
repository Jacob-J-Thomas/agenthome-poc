using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Persists versioned custom-loop definitions and idempotent authoring-operation receipts.
/// </summary>
public interface ICustomLoopDefinitionStore
{
    /// <summary>
    /// Creates a definition when its identifier is not already present.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The persisted definition or the detected identifier conflict.</returns>
    Task<CustomLoopDefinitionStoreResult> CreateAsync(CustomLoopDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a definition and atomically records the supplied idempotent mutation receipt.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="mutation">The mutation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The persisted definition, replayed result, or request conflict.</returns>
    Task<CustomLoopDefinitionStoreResult> CreateAsync(CustomLoopDefinition definition, CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken = default) => CreateAsync(definition, cancellationToken);

    /// <summary>
    /// Loads the durable create-operation receipt for an idempotency identifier.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The stored receipt or a not-found result.</returns>
    Task<CustomLoopCreateOperationLookupResult> GetCreateOperationAsync(string operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a durable create, update, or delete mutation receipt.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The stored receipt or a not-found result.</returns>
    Task<CustomLoopDefinitionMutationLookupResult> GetMutationOperationAsync(string operationId, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopDefinitionMutationLookupResult.NotFound());

    /// <summary>
    /// Loads the current definition for a loop.
    /// </summary>
    /// <param name="loopId">The loop ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The current definition, or <see langword="null"/> when it does not exist.</returns>
    Task<CustomLoopDefinition?> GetAsync(string loopId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists current, non-deleted definitions.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The current definitions in the store's deterministic order.</returns>
    Task<IReadOnlyList<CustomLoopDefinition>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a definition only when its persisted version matches the expected version.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="expectedDefinitionVersion">The expected definition version.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The updated definition or an optimistic-concurrency conflict.</returns>
    Task<CustomLoopDefinitionStoreResult> UpdateAsync(CustomLoopDefinition definition, int expectedDefinitionVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a version-checked update and atomically records its idempotent mutation receipt.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="expectedDefinitionVersion">The expected definition version.</param>
    /// <param name="mutation">The mutation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The updated definition, replayed result, or request/version conflict.</returns>
    Task<CustomLoopDefinitionStoreResult> UpdateAsync(CustomLoopDefinition definition, int expectedDefinitionVersion, CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken = default) => UpdateAsync(definition, expectedDefinitionVersion, cancellationToken);

    /// <summary>
    /// Tombstones a definition only when its persisted version matches the expected version.
    /// </summary>
    /// <param name="loopId">The loop ID.</param>
    /// <param name="expectedDefinitionVersion">The expected definition version.</param>
    /// <param name="mutationOperationId">The mutation operation ID.</param>
    /// <param name="deletedAtUtc">The deleted at UTC.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The deletion result or an optimistic-concurrency conflict.</returns>
    Task<CustomLoopDefinitionStoreResult> DeleteAsync(string loopId, int expectedDefinitionVersion, string mutationOperationId, DateTimeOffset deletedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a version-checked tombstone and atomically records its idempotent mutation receipt.
    /// </summary>
    /// <param name="loopId">The loop ID.</param>
    /// <param name="expectedDefinitionVersion">The expected definition version.</param>
    /// <param name="mutationOperationId">The mutation operation ID.</param>
    /// <param name="deletedAtUtc">The deleted at UTC.</param>
    /// <param name="mutation">The mutation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The deletion result, replayed result, or request/version conflict.</returns>
    Task<CustomLoopDefinitionStoreResult> DeleteAsync(string loopId, int expectedDefinitionVersion, string mutationOperationId, DateTimeOffset deletedAtUtc, CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken = default) => DeleteAsync(loopId, expectedDefinitionVersion, mutationOperationId, deletedAtUtc, cancellationToken);

    /// <summary>
    /// Marks the terminal mutation outcome as durably audited.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>Whether the marker was applied, already present, or the operation was not found.</returns>
    Task<CustomLoopOperationAuditMarkStatus> MarkOperationOutcomeAuditedAsync(string operationId, CancellationToken cancellationToken = default);
}
