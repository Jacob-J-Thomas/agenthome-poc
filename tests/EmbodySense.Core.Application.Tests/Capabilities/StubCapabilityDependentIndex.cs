using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class StubCapabilityDependentIndex : ICapabilityDependentIndex
{
    internal CapabilityDependentIndexSnapshot Snapshot { get; set; } = new(CapabilityDependentIndexStatus.Available, CapabilityArtifactTestData.Manifest().Checksum.Value, [], "available");
    internal Queue<CapabilityDependentIndexSnapshot> Snapshots { get; } = new();
    internal int CaptureCount { get; private set; }

    public Task<CapabilityDependentIndexSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        CaptureCount++;
        return Task.FromResult(Snapshots.Count == 0 ? Snapshot : Snapshots.Dequeue());
    }
}
