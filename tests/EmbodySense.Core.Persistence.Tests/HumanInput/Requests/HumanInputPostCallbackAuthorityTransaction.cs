using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Requests;

internal sealed class HumanInputPostCallbackAuthorityTransaction(Exception releaseFailure) : ICapabilityAuthorityTransaction
{
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        _ = await operation(cancellationToken);
        throw releaseFailure;
    }

    public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(
        Func<CancellationToken, Task<bool>> validator,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
