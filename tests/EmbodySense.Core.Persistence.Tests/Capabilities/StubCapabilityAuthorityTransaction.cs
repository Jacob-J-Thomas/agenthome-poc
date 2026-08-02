using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class StubCapabilityAuthorityTransaction : ICapabilityAuthorityTransaction
{
    public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default) => await operation(cancellationToken);

    public async Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default) => await validator(cancellationToken) ? new StubCapabilityAuthorityLease() : null;
}
