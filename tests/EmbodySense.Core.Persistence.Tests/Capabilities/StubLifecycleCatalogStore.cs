using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class StubLifecycleCatalogStore : ICapabilityCatalogStore
{
    internal CapabilityCatalogReadResult ReadResult { get; set; } = new(CapabilityCatalogReadStatus.Unavailable, null, "unavailable");
    internal int? LastMaximumCount { get; private set; }
    public Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
    {
        LastMaximumCount = maximumCount;
        return Task.FromResult(ReadResult);
    }
    public Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
