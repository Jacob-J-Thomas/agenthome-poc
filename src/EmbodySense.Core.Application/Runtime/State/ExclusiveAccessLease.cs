namespace EmbodySense.Core.Application.Runtime.State;

internal sealed class ExclusiveAccessLease : IDisposable
{
    private SemaphoreSlim? _gate;
    private IDisposable? _workspaceLease;

    public ExclusiveAccessLease(SemaphoreSlim gate, IDisposable? workspaceLease)
    {
        _gate = gate;
        _workspaceLease = workspaceLease;
    }

    public void Dispose()
    {
        try
        {
            Interlocked.Exchange(ref _workspaceLease, null)?.Dispose();
        }
        finally
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
