using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.IntegrationTests.Core.Capabilities;

internal sealed class SignalingCapabilityAuthorityTransaction(ICapabilityAuthorityTransaction inner) : ICapabilityAuthorityTransaction
{
    private readonly TaskCompletionSource _executionAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task ExecutionAttempted => _executionAttempted.Task;

    public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        _executionAttempted.TrySetResult();
        return inner.ExecuteAsync(operation, cancellationToken);
    }

    public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
    {
        return inner.AcquireValidatedLeaseAsync(validator, cancellationToken);
    }
}
