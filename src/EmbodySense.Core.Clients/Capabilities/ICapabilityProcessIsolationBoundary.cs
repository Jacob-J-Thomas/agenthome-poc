using System.Diagnostics;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Clients.Capabilities;

/// <summary>Represents trusted platform infrastructure that starts a process only after OS isolation controls are enforceable.</summary>
/// <remarks>Implementations must enforce process-tree, memory, filesystem, data, and network boundaries before child code can run; returning a normally started process and attaching controls later is invalid. The host invokes this interface only on Windows, where the retained lease denies replacement of the executable pathname. Other platforms remain unavailable until they have a handle-bound launch seam.</remarks>
public interface ICapabilityProcessIsolationBoundary
{
    /// <summary>Checks whether this boundary can enforce every declaration in one manifest.</summary>
    CapabilityExecutableAvailability CheckAvailability(CapabilityArtifactManifest manifest);

    /// <summary>Starts the exact retained executable with all declared isolation controls already applied.</summary>
    /// <remarks>An adapter must bind creation to <paramref name="artifactLease"/>'s retained handle rather than trusting <see cref="ProcessStartInfo.FileName"/> alone, and must fail closed when its platform cannot do so.</remarks>
    Process StartIsolated(ProcessStartInfo startInfo, CapabilityArtifactManifest manifest, ICapabilityExecutableArtifactLease artifactLease);
}
