using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Resolves a caller request to an activation-bound executable lease without trusting caller paths.</summary>
public interface ICapabilityExecutableArtifactResolver
{
    /// <summary>Resolves only the current proved activation matching the supplied manifest and revision.</summary>
    /// <remarks>The returned lease retains its exact filesystem identity until the host disposes it.</remarks>
    Task<CapabilityExecutableArtifactResolution> ResolveAsync(CapabilityExecutableInvocation invocation, CancellationToken cancellationToken = default);
}
