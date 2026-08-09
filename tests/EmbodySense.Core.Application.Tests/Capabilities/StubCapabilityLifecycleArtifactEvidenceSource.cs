using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class StubCapabilityLifecycleArtifactEvidenceSource : ICapabilityLifecycleArtifactEvidenceSource
{
    internal CapabilityLifecycleArtifactEvidence Evidence { get; set; } = new(CapabilityLifecycleArtifactEvidenceStatus.Proved, "proved");
    internal CapabilityDescriptor? Descriptor { get; private set; }

    public Task<CapabilityLifecycleArtifactEvidence> VerifyAsync(CapabilityDescriptor descriptor, CapabilityIntegrityDigest artifactDigest, CancellationToken cancellationToken = default)
    {
        Descriptor = descriptor;
        return Task.FromResult(Evidence);
    }
}
