using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class BlockingCoordinatorEvidencePort(IGovernedLoopCoordinatorEvidencePort inner) : IGovernedLoopCoordinatorEvidencePort
{
    private readonly TaskCompletionSource _failureEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _failureRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task FailureEntered => _failureEntered.Task;

    internal void ReleaseFailure() => _failureRelease.TrySetResult();

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

    public Task<GovernedLoopCoordinatorLifecycleMutationResult?> AppendLifecycleAsync(
        GovernedLoopCoordinatorLifecycleMutationRequest request,
        CancellationToken cancellationToken = default)
        => inner.AppendLifecycleAsync(request, cancellationToken);

    public async Task<GovernedLoopCoordinatorFailureMutationResult?> AppendFailureAsync(
        GovernedLoopCoordinatorFailureMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        _failureEntered.TrySetResult();
        await _failureRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await inner.AppendFailureAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
