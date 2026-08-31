using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Loops.Admission;

internal sealed class ThrowAfterCallbackAuthorityTransaction : ICapabilityAuthorityTransaction
{
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        _ = await operation(cancellationToken);
        throw new IOException("Injected authority-release failure.");
    }

    public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(
        Func<CancellationToken, Task<bool>> validator,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
