namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class CustomLoopRunCanonicalPublisherTestTimer(
    CustomLoopRunCanonicalPublisherTestTimeProvider owner,
    TimerCallback callback,
    object? state,
    long dueTimestamp) : ITimer
{
    private readonly object _gate = new();
    private long _dueTimestamp = dueTimestamp;
    private bool _disposed;

    internal bool IsDue(long timestamp)
    {
        lock (_gate)
        {
            return !_disposed && timestamp >= _dueTimestamp;
        }
    }

    internal void Fire(long timestamp)
    {
        lock (_gate)
        {
            if (_disposed || timestamp < _dueTimestamp)
            {
                return;
            }

            _disposed = true;
        }

        callback(state);
    }

    public bool Change(TimeSpan dueTime, TimeSpan period) => owner.Change(this, dueTime, period);

    internal bool ChangeCore(long dueTimestamp)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _dueTimestamp = dueTimestamp;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
