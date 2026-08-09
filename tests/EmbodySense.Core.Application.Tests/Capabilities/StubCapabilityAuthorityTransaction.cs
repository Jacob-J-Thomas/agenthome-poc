using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class StubCapabilityAuthorityTransaction : ICapabilityAuthorityTransaction
{
    internal int Executions { get; private set; }

    public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        Executions++;
        return await operation(cancellationToken);
    }

    public async Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default) => await validator(cancellationToken) ? new StubCapabilityAuthorityLease() : null;
}
