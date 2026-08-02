using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class StubPackageDependencyManifestDiscovery : ICapabilityPackageDependencyManifestDiscovery
{
    internal IReadOnlyList<CapabilityPackageDependencyDiscovery> Discoveries { get; init; } = [];
    public Task<IReadOnlyList<CapabilityPackageDependencyDiscovery>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(Discoveries);
}
