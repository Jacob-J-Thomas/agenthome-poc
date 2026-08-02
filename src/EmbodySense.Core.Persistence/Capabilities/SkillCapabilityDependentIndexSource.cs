using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Indexes current local skill sidecars as metadata-only dependency evidence.</summary>
public sealed class SkillCapabilityDependentIndexSource : ICapabilityDependentIndexSource
{
    private readonly ISkillDependencyManifestDiscovery _discovery;

    /// <summary>Creates the real local-skill dependent adapter.</summary>
    public SkillCapabilityDependentIndexSource(ISkillDependencyManifestDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        _discovery = discovery;
    }

    /// <inheritdoc />
    public string Name => "skills";

    /// <inheritdoc />
    public async Task<IReadOnlyList<CapabilityDependent>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var dependents = new List<CapabilityDependent>();
        foreach (var discovered in await _discovery.DiscoverAsync(cancellationToken))
        {
            if (discovered is null)
            {
                throw new FormatException("Skill discovery returned a null dependency record.");
            }
            if (discovered.Status == LocalSkillDependencyDiscoveryStatus.NoManifest)
            {
                continue;
            }
            if (discovered.Status != LocalSkillDependencyDiscoveryStatus.Discovered || discovered.Manifest is null || discovered.Manifest.Kind != EmbodySense.Core.Common.Capabilities.Models.CapabilityDependencyManifestKind.Skill || !CapabilityDependencyManifestHash.TryCompute(discovered.Manifest, out var hash, out _))
            {
                throw new FormatException($"Skill '{discovered.DirectoryName}' has unproved capability dependency evidence.");
            }
            dependents.Add(new CapabilityDependent(CapabilityDependentKind.Skill, discovered.DirectoryName, hash!.Value, discovered.Manifest, CapabilityAuthorityPosture.MetadataOnly));
        }
        return dependents;
    }
}
