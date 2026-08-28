using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Clients.Tests.CommandActions;

internal sealed class ReentrantCurrentArtifactResolver(
    string artifactRoot,
    CapabilityIntegrityDigest artifactDigest,
    long activationRevision) : ICapabilityExecutableArtifactResolver
{
    internal bool Current { get; set; } = true;

    internal int ExecuteLaunchFenceCalls { get; private set; }

    internal int AcquireLaunchFenceCalls { get; private set; }

    public Task<CapabilityExecutableArtifactResolution> ResolveAsync(
        CapabilityExecutableInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ICapabilityExecutableArtifactLease lease = new ReentrantCurrentArtifactLease(this, artifactRoot, invocation.Manifest.EntryPoint, artifactDigest, activationRevision);
        return Task.FromResult(new CapabilityExecutableArtifactResolution(
            CapabilityExecutableAvailabilityStatus.Available,
            lease,
            "Reentrant current test artifact."));
    }

    internal void RecordExecuteLaunchFence() => ExecuteLaunchFenceCalls++;

    internal void RecordAcquireLaunchFence() => AcquireLaunchFenceCalls++;
}
