using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class StubCapabilityAuthorityTransaction : ICapabilityAuthorityTransaction
{
    internal int Executions { get; private set; }

    internal Exception? Exception { get; set; }

    public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        Executions++;
        if (Exception is not null)
        {
            throw Exception;
        }

        return await operation(cancellationToken);
    }

    public async Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default) => await validator(cancellationToken) ? new StubCapabilityAuthorityLease() : null;
}
