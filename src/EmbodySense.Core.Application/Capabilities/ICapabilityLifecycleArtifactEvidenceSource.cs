using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Proves exact staged immutable artifact targets without activating or granting them.</summary>
public interface ICapabilityLifecycleArtifactEvidenceSource
{
    /// <summary>Checks one exact descriptor and artifact digest against retained server-authenticated staging evidence.</summary>
    Task<CapabilityLifecycleArtifactEvidence> VerifyAsync(CapabilityDescriptor descriptor, CapabilityIntegrityDigest artifactDigest, CancellationToken cancellationToken = default);
}
