using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;

internal sealed class HumanInputContinuationAuthorityTransaction : ICapabilityAuthorityTransaction
{
    public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        => operation(cancellationToken);

    public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The response seed only requires the bounded execute path.");
}
