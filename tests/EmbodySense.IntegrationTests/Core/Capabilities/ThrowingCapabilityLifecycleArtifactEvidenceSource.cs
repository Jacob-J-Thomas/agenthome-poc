using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.IntegrationTests.Core.Capabilities;

internal sealed class ThrowingCapabilityLifecycleArtifactEvidenceSource : ICapabilityLifecycleArtifactEvidenceSource
{
    public Task<CapabilityLifecycleArtifactEvidence> VerifyAsync(CapabilityDescriptor descriptor, CapabilityIntegrityDigest artifactDigest, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Artifact verification is outside disable lifecycle mutation ordering.");
    }
}
