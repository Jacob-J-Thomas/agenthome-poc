using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Reads dependency manifests from currently activated immutable capability packages.</summary>
public interface ICapabilityPackageDependencyManifestDiscovery
{
    /// <summary>Returns the complete activated package dependency set or throws when it cannot be proved.</summary>
    Task<IReadOnlyList<CapabilityPackageDependencyDiscovery>> DiscoverAsync(CancellationToken cancellationToken = default);
}
