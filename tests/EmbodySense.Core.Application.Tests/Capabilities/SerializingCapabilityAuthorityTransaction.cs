using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class SerializingCapabilityAuthorityTransaction : ICapabilityAuthorityTransaction
{
    private readonly AsyncLocal<int> _depth = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        if (_depth.Value > 0)
        {
            return await operation(cancellationToken);
        }

        await _gate.WaitAsync(cancellationToken);
        _depth.Value++;
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            _depth.Value--;
            _gate.Release();
        }
    }

    public async Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!await validator(cancellationToken))
            {
                _gate.Release();
                return null;
            }

            return new SerializingCapabilityAuthorityLease(_gate);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }
}
