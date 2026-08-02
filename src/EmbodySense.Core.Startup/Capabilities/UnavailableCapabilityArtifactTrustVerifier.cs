using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Startup.Capabilities;

internal sealed class UnavailableCapabilityArtifactTrustVerifier : ICapabilityArtifactTrustVerifier
{
    internal static UnavailableCapabilityArtifactTrustVerifier Instance { get; } = new();

    public Task<CapabilityArtifactTrustDecision> VerifyAsync(CapabilityArtifactManifest manifest, CapabilityIntegrityDigest actualDigest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(actualDigest);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Unavailable, "posture-read-only-v1", "Read-only capability posture never verifies or activates artifacts."));
    }
}
