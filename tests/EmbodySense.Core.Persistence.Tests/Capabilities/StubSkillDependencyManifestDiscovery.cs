using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class StubSkillDependencyManifestDiscovery : ISkillDependencyManifestDiscovery
{
    internal IReadOnlyList<LocalSkillDependencyDiscovery> Discoveries { get; init; } = [];
    public Task<IReadOnlyList<LocalSkillDependencyDiscovery>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(Discoveries);
}
