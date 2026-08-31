using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessAuthorityTransaction : ICapabilityAuthorityTransaction
{
    public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return operation(cancellationToken);
    }

    public async Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validator);
        return await validator(cancellationToken) ? HumanReviewOrderedReleaseProcessAuthorityLease.Instance : null;
    }
}
