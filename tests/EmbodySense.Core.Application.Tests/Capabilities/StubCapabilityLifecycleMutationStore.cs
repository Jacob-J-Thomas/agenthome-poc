using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class StubCapabilityLifecycleMutationStore : ICapabilityLifecycleMutationStore
{
    internal CapabilityLifecycleReadResult ReadResult { get; set; } = new(CapabilityLifecycleReadStatus.NotFound, null, [], [], null, "not found");
    internal Exception? ReadException { get; set; }
    internal CapabilityLifecyclePreview PreviewResult { get; set; } = null!;
    internal CapabilityLifecyclePreview? SelectionReplayResult { get; set; }
    internal CapabilityLifecycleMutationResult MutationResult { get; set; } = null!;
    internal CapabilityLifecycleAuditMarkStatus AuditMarkResult { get; set; } = CapabilityLifecycleAuditMarkStatus.Applied;
    internal CapabilityLifecyclePreviewRequest? PreviewRequest { get; private set; }
    internal CapabilityLifecycleSelectionRequest? SelectionReplayRequest { get; private set; }
    internal CapabilityLifecycleBaseline? Baseline { get; private set; }
    internal CapabilityLifecycleBaseline? MutatedBaseline { get; private set; }
    internal CapabilityLifecyclePreview? MutatedPreview { get; private set; }
    internal CapabilityDependentIndexSnapshot? PreviewDependents { get; private set; }
    internal CapabilityDependentIndexSnapshot? MutatedDependents { get; private set; }
    internal int AuditMarks { get; private set; }
    internal int ReadCount { get; private set; }
    internal int PreviewCount { get; private set; }

    public Task<CapabilityLifecycleReadResult> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        if (ReadException is not null)
        {
            throw ReadException;
        }
        return Task.FromResult(ReadResult);
    }

    public Task<CapabilityLifecyclePreview> PreviewAsync(CapabilityLifecyclePreviewRequest request, CapabilityLifecycleBaseline? baseline, CapabilityDependentIndexSnapshot dependents, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PreviewCount++;
        PreviewRequest = request;
        Baseline = baseline;
        PreviewDependents = dependents;
        return Task.FromResult(PreviewResult);
    }

    public Task<CapabilityLifecyclePreview> TryReplaySelectionAsync(CapabilityLifecycleSelectionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SelectionReplayRequest = request;
        return Task.FromResult(SelectionReplayResult ?? new CapabilityLifecyclePreview(CapabilityLifecyclePreviewStatus.NotFound, "sha256:workspace", request.OperationId, request.Kind, request.CapabilityId, 0, 0, string.Empty, string.Empty, [], "not persisted"));
    }

    public Task<CapabilityLifecycleMutationResult> MutateAsync(CapabilityLifecyclePreview preview, CapabilityLifecycleBaseline? baseline, CapabilityDependentIndexSnapshot dependents, CancellationToken cancellationToken = default)
    {
        MutatedPreview = preview;
        MutatedBaseline = baseline;
        MutatedDependents = dependents;
        return Task.FromResult(MutationResult);
    }

    public Task<CapabilityLifecycleAuditMarkStatus> MarkOutcomeAuditedAsync(string operationId, CancellationToken cancellationToken = default)
    {
        AuditMarks++;
        return Task.FromResult(AuditMarkResult);
    }
}
