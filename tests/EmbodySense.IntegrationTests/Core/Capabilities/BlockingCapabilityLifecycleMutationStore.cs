using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.IntegrationTests.Core.Capabilities;

internal sealed class BlockingCapabilityLifecycleMutationStore : ICapabilityLifecycleMutationStore
{
    private readonly TaskCompletionSource _mutationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _mutationRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal BlockingCapabilityLifecycleMutationStore(bool initiallyReleased = false)
    {
        if (initiallyReleased)
        {
            _mutationRelease.SetResult();
        }
    }

    internal Task MutationEntered => _mutationEntered.Task;

    internal void ReleaseMutation() => _mutationRelease.TrySetResult();

    public Task<CapabilityLifecycleReadResult> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Lifecycle reads are outside this ordering test.");
    }

    public Task<CapabilityLifecyclePreview> PreviewAsync(CapabilityLifecyclePreviewRequest request, CapabilityLifecycleBaseline? baseline, CapabilityDependentIndexSnapshot dependents, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Lifecycle previews are outside this ordering test.");
    }

    public async Task<CapabilityLifecycleMutationResult> MutateAsync(CapabilityLifecyclePreview preview, CapabilityLifecycleBaseline? baseline, CapabilityDependentIndexSnapshot dependents, CancellationToken cancellationToken = default)
    {
        _mutationEntered.TrySetResult();
        await _mutationRelease.Task.WaitAsync(cancellationToken);
        return new CapabilityLifecycleMutationResult(CapabilityLifecycleMutationStatus.Applied, null, preview.LifecycleRevision + 1, false, "applied");
    }

    public Task<CapabilityLifecycleAuditMarkStatus> MarkOutcomeAuditedAsync(string operationId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CapabilityLifecycleAuditMarkStatus.NoChange);
    }
}
