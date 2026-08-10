using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class StubCapabilityLifecycleMutationStore : ICapabilityLifecycleMutationStore
{
    internal CapabilityLifecycleReadResult ReadResult { get; set; } = new(CapabilityLifecycleReadStatus.NotFound, null, [], [], null, "not found");

    public Task<CapabilityLifecycleReadResult> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default) => Task.FromResult(ReadResult);

    public Task<CapabilityLifecyclePreview> PreviewAsync(CapabilityLifecyclePreviewRequest request, CapabilityLifecycleBaseline? baseline, CapabilityDependentIndexSnapshot dependents, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CapabilityLifecyclePreview> TryReplaySelectionAsync(CapabilityLifecycleSelectionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CapabilityLifecycleMutationResult> MutateAsync(CapabilityLifecyclePreview preview, CapabilityLifecycleBaseline? baseline, CapabilityDependentIndexSnapshot dependents, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CapabilityLifecycleMutationResult> DiscardAsync(CapabilityLifecyclePreview preview, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CapabilityLifecycleAuditMarkStatus> MarkOutcomeAuditedAsync(string operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
