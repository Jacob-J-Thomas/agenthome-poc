using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class StubLifecycleArtifactStore : ICapabilityArtifactStore
{
    internal CapabilityArtifactStoreResult ReadResult { get; set; } = new(CapabilityArtifactStoreStatus.NotFound, null, "not found");
    public Task<CapabilityArtifactStoreResult> StageAsync(CapabilityArtifactStageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<CapabilityArtifactStoreResult> ActivateAsync(CapabilityArtifactActivationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<CapabilityArtifactStoreResult> RollbackAsync(CapabilityId capabilityId, long expectedRevision, string operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<CapabilityArtifactStoreResult> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default) => Task.FromResult(ReadResult);
    public Task<CapabilityExecutableArtifactResolution> ResolveAsync(CapabilityExecutableInvocation invocation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
