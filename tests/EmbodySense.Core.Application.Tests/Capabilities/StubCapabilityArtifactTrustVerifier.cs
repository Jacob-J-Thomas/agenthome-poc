using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class StubCapabilityArtifactTrustVerifier : ICapabilityArtifactTrustVerifier
{
    internal CapabilityArtifactTrustDecision Decision { get; set; } = new(CapabilityArtifactTrustStatus.Verified, "test-policy", "Verified.");
    internal int Calls { get; private set; }

    public Task<CapabilityArtifactTrustDecision> VerifyAsync(CapabilityArtifactManifest manifest, CapabilityIntegrityDigest actualDigest, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Decision);
    }
}
