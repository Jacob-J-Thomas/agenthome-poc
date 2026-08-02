using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Indexes current built-in and custom loop dependency manifests without changing loop-owned assignment semantics.</summary>
public sealed class LoopCapabilityDependentIndexSource : ICapabilityDependentIndexSource
{
    private readonly ILoopDefinitionStore _loopStore;
    private readonly ICustomLoopDefinitionStore _customLoopStore;

    /// <summary>Creates the real loop-domain dependent adapter.</summary>
    public LoopCapabilityDependentIndexSource(ILoopDefinitionStore loopStore, ICustomLoopDefinitionStore customLoopStore)
    {
        ArgumentNullException.ThrowIfNull(loopStore);
        ArgumentNullException.ThrowIfNull(customLoopStore);
        _loopStore = loopStore;
        _customLoopStore = customLoopStore;
    }

    /// <inheritdoc />
    public string Name => "loops";

    /// <inheritdoc />
    public async Task<IReadOnlyList<CapabilityDependent>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var dependents = new List<CapabilityDependent>();
        foreach (var loop in await _loopStore.ListAsync(cancellationToken))
        {
            if (loop is null || loop.CapabilityRequirements is null || loop.CapabilityRequirements.Kind != EmbodySense.Core.Common.Capabilities.Models.CapabilityDependencyManifestKind.LoopPackage || !CapabilityDependencyManifestHash.TryCompute(loop.CapabilityRequirements, out var hash, out _))
            {
                throw new FormatException($"Loop '{loop?.Id ?? "unknown"}' has invalid capability dependency evidence.");
            }
            dependents.Add(new CapabilityDependent(CapabilityDependentKind.Loop, loop.Id, hash!.Value, loop.CapabilityRequirements, CapabilityAuthorityPosture.AssignedDefinition));
        }
        foreach (var loop in await _customLoopStore.ListAsync(cancellationToken))
        {
            if (loop is null || loop.CapabilityRequirements is null || loop.CapabilityRequirements.Kind != EmbodySense.Core.Common.Capabilities.Models.CapabilityDependencyManifestKind.LoopPackage || !CapabilityDependencyManifestHash.TryCompute(loop.CapabilityRequirements, out _, out _))
            {
                throw new FormatException($"Custom loop '{loop?.Id ?? "unknown"}' has invalid capability dependency evidence.");
            }
            dependents.Add(new CapabilityDependent(CapabilityDependentKind.Loop, loop.Id, $"{loop.DefinitionVersion}:{loop.ContentHash}", loop.CapabilityRequirements, CapabilityAuthorityPosture.AssignedDefinition));
        }
        return dependents;
    }
}
