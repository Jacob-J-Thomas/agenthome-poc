using System.Diagnostics;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Clients.Capabilities;

/// <summary>Fails closed when no platform isolation adapter has been configured.</summary>
public sealed class DenyingCapabilityProcessIsolationBoundary : ICapabilityProcessIsolationBoundary
{
    /// <summary>Gets the reusable fail-closed boundary.</summary>
    public static DenyingCapabilityProcessIsolationBoundary Instance { get; } = new();

    private DenyingCapabilityProcessIsolationBoundary()
    {
    }

    /// <inheritdoc />
    public CapabilityExecutableAvailability CheckAvailability(CapabilityArtifactManifest manifest) => new(CapabilityExecutableAvailabilityStatus.Unavailable, "No platform process-isolation adapter is configured; executable capabilities remain unavailable.");

    /// <inheritdoc />
    public Process StartIsolated(ProcessStartInfo startInfo, CapabilityArtifactManifest manifest, ICapabilityExecutableArtifactLease artifactLease) => throw new PlatformNotSupportedException("No platform process-isolation adapter is configured.");
}
