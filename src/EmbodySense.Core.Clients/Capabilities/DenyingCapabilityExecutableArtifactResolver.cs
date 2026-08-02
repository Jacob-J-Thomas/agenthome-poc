using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Clients.Capabilities;

/// <summary>Fails closed until executable hosting is connected to a proved artifact activation resolver.</summary>
public sealed class DenyingCapabilityExecutableArtifactResolver : ICapabilityExecutableArtifactResolver
{
    /// <summary>Gets the reusable fail-closed resolver.</summary>
    public static DenyingCapabilityExecutableArtifactResolver Instance { get; } = new();

    private DenyingCapabilityExecutableArtifactResolver()
    {
    }

    /// <inheritdoc />
    public Task<CapabilityExecutableArtifactResolution> ResolveAsync(CapabilityExecutableInvocation invocation, CancellationToken cancellationToken = default) => Task.FromResult(new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Unavailable, null, "No proved artifact activation resolver is configured; executable capabilities remain unavailable."));
}
