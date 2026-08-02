using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class TestCapabilityArtifactTrustVerifier : ICapabilityArtifactTrustVerifier
{
    public Task<CapabilityArtifactTrustDecision> VerifyAsync(CapabilityArtifactManifest manifest, CapabilityIntegrityDigest actualDigest, CancellationToken cancellationToken = default) => Task.FromResult(new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Verified, "test-server-policy", "Verified."));
}
