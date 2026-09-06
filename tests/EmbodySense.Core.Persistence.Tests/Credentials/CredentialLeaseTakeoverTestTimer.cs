namespace EmbodySense.Core.Persistence.Tests.Credentials;

internal sealed class CredentialLeaseTakeoverTestTimer(CredentialLeaseTakeoverTestTimeProvider owner, TimerCallback callback, object? state) : ITimer
{
    private long _dueAtTimestamp = long.MaxValue;
    private TimeSpan _period = Timeout.InfiniteTimeSpan;
    private bool _disposed;

    public bool Change(TimeSpan dueTime, TimeSpan period) => owner.Change(this, dueTime, period);

    public void Dispose() => owner.Dispose(this);

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal bool ChangeCore(long dueAtTimestamp, TimeSpan period)
    {
        if (_disposed)
        {
            return false;
        }

        _dueAtTimestamp = dueAtTimestamp;
        _period = period;
        return true;
    }

    internal void DisposeCore() => _disposed = true;

    internal bool TryFireCore(long timestamp, out TimerCallback? dueCallback, out object? dueState)
    {
        if (_disposed || timestamp < _dueAtTimestamp)
        {
            dueCallback = null;
            dueState = null;
            return false;
        }

        if (_period == Timeout.InfiniteTimeSpan)
        {
            _disposed = true;
        }
        else
        {
            _dueAtTimestamp = checked(timestamp + _period.Ticks);
        }
        dueCallback = callback;
        dueState = state;
        return true;
    }
}
