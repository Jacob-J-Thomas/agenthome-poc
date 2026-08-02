using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class StubCapabilityLifecycleArtifactEvidenceSource : ICapabilityLifecycleArtifactEvidenceSource
{
    internal CapabilityLifecycleArtifactEvidence Evidence { get; set; } = new(CapabilityLifecycleArtifactEvidenceStatus.Proved, "proved");
    internal int Verifications { get; private set; }

    public Task<CapabilityLifecycleArtifactEvidence> VerifyAsync(CapabilityDescriptor descriptor, CapabilityIntegrityDigest artifactDigest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Verifications++;
        return Task.FromResult(Evidence);
    }
}
