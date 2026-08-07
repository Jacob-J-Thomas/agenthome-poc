using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Discovers bounded local skill dependency sidecars without registering or executing them.</summary>
public interface ISkillDependencyManifestDiscovery
{
    /// <summary>Reads only the configured local skills scope.</summary>
    Task<IReadOnlyList<LocalSkillDependencyDiscovery>> DiscoverAsync(CancellationToken cancellationToken = default);
}
