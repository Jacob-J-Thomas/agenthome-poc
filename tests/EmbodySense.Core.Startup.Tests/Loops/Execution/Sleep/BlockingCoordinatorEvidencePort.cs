using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class BlockingCoordinatorEvidencePort(IGovernedLoopCoordinatorEvidencePort inner) : IGovernedLoopCoordinatorEvidencePort
{
    private readonly TaskCompletionSource _failureEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _failureRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _failedLifecyclePersisted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _failedLifecycleRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task FailureEntered => _failureEntered.Task;

    internal Task FailedLifecyclePersisted => _failedLifecyclePersisted.Task;

    internal bool BlockFailureBeforeCommit { get; set; } = true;

    internal bool BlockFailedLifecycleAfterCommit { get; set; }

    internal void ReleaseFailure() => _failureRelease.TrySetResult();

    internal void ReleaseFailedLifecycle() => _failedLifecycleRelease.TrySetResult();

    public Task<GovernedLoopCoordinatorReadResult?> ReadAsync(
        string coordinatorId,
        CancellationToken cancellationToken = default)
        => inner.ReadAsync(coordinatorId, cancellationToken);

    public Task<GovernedLoopCoordinatorAcquisitionResult?> TryAcquireAsync(
        GovernedLoopCoordinatorAcquisitionRequest request,
        CancellationToken cancellationToken = default)
        => inner.TryAcquireAsync(request, cancellationToken);

    public Task<GovernedLoopCoordinatorHeartbeatMutationResult?> RenewHeartbeatAsync(
        GovernedLoopCoordinatorHeartbeatMutationRequest request,
        CancellationToken cancellationToken = default)
        => inner.RenewHeartbeatAsync(request, cancellationToken);

    public async Task<GovernedLoopCoordinatorLifecycleMutationResult?> AppendLifecycleAsync(
        GovernedLoopCoordinatorLifecycleMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.AppendLifecycleAsync(request, cancellationToken).ConfigureAwait(false);
        if (BlockFailedLifecycleAfterCommit
            && request.ProposedLifecycle.Status == GovernedLoopCoordinatorStatus.Failed
            && result?.Status is GovernedLoopCoordinatorLifecycleMutationStatus.Appended
                or GovernedLoopCoordinatorLifecycleMutationStatus.Duplicate)
        {
            _failedLifecyclePersisted.TrySetResult();
            await _failedLifecycleRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<GovernedLoopCoordinatorFailureMutationResult?> AppendFailureAsync(
        GovernedLoopCoordinatorFailureMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        _failureEntered.TrySetResult();
        if (BlockFailureBeforeCommit)
        {
            await _failureRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await inner.AppendFailureAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
