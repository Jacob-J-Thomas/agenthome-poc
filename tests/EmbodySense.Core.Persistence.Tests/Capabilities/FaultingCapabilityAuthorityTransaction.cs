using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

public enum CapabilityAuthorityTransactionFault
{
    CancelBeforeCallback,
    CancelAfterCallback,
    IoAfterCallback
}

internal sealed class FaultingCapabilityAuthorityTransaction(
    ICapabilityAuthorityTransaction inner,
    CapabilityAuthorityTransactionFault fault) : ICapabilityAuthorityTransaction
{
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        if (fault == CapabilityAuthorityTransactionFault.CancelBeforeCallback)
        {
            throw new OperationCanceledException("Injected cancellation before the authority callback.");
        }

        _ = await inner.ExecuteAsync(operation, cancellationToken);
        throw fault switch
        {
            CapabilityAuthorityTransactionFault.CancelAfterCallback => new OperationCanceledException("Injected cancellation after the authority callback."),
            CapabilityAuthorityTransactionFault.IoAfterCallback => new IOException("Injected authority-fence release failure after the callback."),
            _ => new InvalidOperationException("Unsupported authority-transaction fault.")
        };
    }

    public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(
        Func<CancellationToken, Task<bool>> validator,
        CancellationToken cancellationToken = default)
        => inner.AcquireValidatedLeaseAsync(validator, cancellationToken);
}
