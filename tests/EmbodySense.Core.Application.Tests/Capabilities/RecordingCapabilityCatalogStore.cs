using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class RecordingCapabilityCatalogStore : ICapabilityCatalogStore
{
    internal List<CapabilityCatalogMutation> Mutations { get; } = [];
    internal CapabilityCatalogReadResult? ReadResult { get; set; }

    public Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ReadResult ?? new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Available, new CapabilityCatalogPage(0, [], null), $"{startAfterId}:{maximumCount}"));
    }

    public Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default)
    {
        Mutations.Add(mutation);
        return Task.FromResult(new CapabilityCatalogMutationResult(CapabilityCatalogMutationStatus.Applied, mutation.OperationId, mutation.ExpectedCatalogRevision + 1, null, mutation.Kind.ToString()));
    }
}
