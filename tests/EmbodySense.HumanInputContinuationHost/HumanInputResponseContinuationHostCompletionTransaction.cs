using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.HumanInputContinuationHost;

internal sealed class HumanInputResponseContinuationHostCompletionTransaction : ICapabilityAuthorityTransaction
{
    internal string? LastFailureDetail { get; private set; }

    public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteCoreAsync(operation, cancellationToken);
    }

    public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validator);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("The bounded continuation completion path never retains a capability-authority lease.");
    }

    private async Task<TResult> ExecuteCoreAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (FormatException exception)
        {
            LastFailureDetail = exception.Message;
            throw;
        }
    }
}
