using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class StubCapabilityArtifactStore : ICapabilityArtifactStore
{
    internal CapabilityArtifactStoreStatus StageStatus { get; set; } = CapabilityArtifactStoreStatus.Applied;
    internal CapabilityArtifactStoreStatus ActivationStatus { get; set; } = CapabilityArtifactStoreStatus.Applied;
    internal int StageCalls { get; private set; }
    internal int ActivationCalls { get; private set; }

    public Task<CapabilityArtifactStoreResult> StageAsync(CapabilityArtifactStageRequest request, CancellationToken cancellationToken = default)
    {
        StageCalls++;
        return Task.FromResult(new CapabilityArtifactStoreResult(StageStatus, null, "Stage result."));
    }

    public Task<CapabilityArtifactStoreResult> ActivateAsync(CapabilityArtifactActivationRequest request, CancellationToken cancellationToken = default)
    {
        ActivationCalls++;
        var activation = new CapabilityArtifactActivation(request.Manifest.Descriptor.Id, request.Manifest.Checksum, null, request.ExpectedRevision + 1, DateTimeOffset.UnixEpoch);
        return Task.FromResult(new CapabilityArtifactStoreResult(ActivationStatus, activation, "Activation result."));
    }

    public Task<CapabilityArtifactStoreResult> RollbackAsync(CapabilityId capabilityId, long expectedRevision, string operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CapabilityArtifactStoreResult> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CapabilityExecutableArtifactResolution> ResolveAsync(CapabilityExecutableInvocation invocation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
