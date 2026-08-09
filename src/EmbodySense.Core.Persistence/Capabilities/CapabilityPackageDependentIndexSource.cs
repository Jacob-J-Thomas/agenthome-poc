using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Indexes currently activated immutable capability packages as historical dependency evidence.</summary>
public sealed class CapabilityPackageDependentIndexSource : ICapabilityDependentIndexSource
{
    private readonly ICapabilityPackageDependencyManifestDiscovery _discovery;

    /// <summary>Creates the real activated-package dependent adapter.</summary>
    public CapabilityPackageDependentIndexSource(ICapabilityPackageDependencyManifestDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        _discovery = discovery;
    }

    /// <inheritdoc />
    public string Name => "packages";

    /// <inheritdoc />
    public async Task<IReadOnlyList<CapabilityDependent>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var dependents = new List<CapabilityDependent>();
        foreach (var package in await _discovery.DiscoverAsync(cancellationToken))
        {
            if (package is null || package.Manifest is null || package.Manifest.Kind != EmbodySense.Core.Common.Capabilities.Models.CapabilityDependencyManifestKind.CapabilityPackage || package.Manifest.SubjectId.Value != package.CapabilityId || !EmbodySense.Core.Common.Capabilities.CapabilityIntegrityDigest.TryParse(package.ArtifactDigest, out _, out _))
            {
                throw new FormatException($"Capability package '{package?.CapabilityId ?? "unknown"}' has forged dependency identity evidence.");
            }
            dependents.Add(new CapabilityDependent(CapabilityDependentKind.Package, package.CapabilityId, package.ArtifactDigest, package.Manifest, CapabilityAuthorityPosture.HistoricalEvidence));
        }
        return dependents;
    }
}
