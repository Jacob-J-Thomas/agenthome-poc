using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops;

public interface ICustomLoopDefinitionStore
{
    Task<CustomLoopDefinitionStoreResult> CreateAsync(CustomLoopDefinition definition, CancellationToken cancellationToken = default);

    Task<CustomLoopDefinitionStoreResult> CreateAsync(CustomLoopDefinition definition, CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken = default) => CreateAsync(definition, cancellationToken);

    Task<CustomLoopCreateOperationLookupResult> GetCreateOperationAsync(string operationId, CancellationToken cancellationToken = default);

    Task<CustomLoopDefinitionMutationLookupResult> GetMutationOperationAsync(string operationId, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopDefinitionMutationLookupResult.NotFound());

    Task<CustomLoopDefinition?> GetAsync(string loopId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomLoopDefinition>> ListAsync(CancellationToken cancellationToken = default);

    Task<CustomLoopDefinitionStoreResult> UpdateAsync(CustomLoopDefinition definition, int expectedDefinitionVersion, CancellationToken cancellationToken = default);

    Task<CustomLoopDefinitionStoreResult> UpdateAsync(CustomLoopDefinition definition, int expectedDefinitionVersion, CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken = default) => UpdateAsync(definition, expectedDefinitionVersion, cancellationToken);

    Task<CustomLoopDefinitionStoreResult> DeleteAsync(string loopId, int expectedDefinitionVersion, string mutationOperationId, DateTimeOffset deletedAtUtc, CancellationToken cancellationToken = default);

    Task<CustomLoopDefinitionStoreResult> DeleteAsync(string loopId, int expectedDefinitionVersion, string mutationOperationId, DateTimeOffset deletedAtUtc, CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken = default) => DeleteAsync(loopId, expectedDefinitionVersion, mutationOperationId, deletedAtUtc, cancellationToken);

    Task<CustomLoopOperationAuditMarkStatus> MarkOperationOutcomeAuditedAsync(string operationId, CancellationToken cancellationToken = default);
}
