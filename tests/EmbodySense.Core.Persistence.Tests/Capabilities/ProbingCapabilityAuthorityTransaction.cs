using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class ProbingCapabilityAuthorityTransaction(ICapabilityAuthorityTransaction inner) : ICapabilityAuthorityTransaction
{
    internal TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        Attempted.TrySetResult();
        return inner.ExecuteAsync(operation, cancellationToken);
    }

    public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
    {
        Attempted.TrySetResult();
        return inner.AcquireValidatedLeaseAsync(validator, cancellationToken);
    }
}
