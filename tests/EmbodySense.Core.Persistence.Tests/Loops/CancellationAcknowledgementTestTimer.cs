namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class CancellationAcknowledgementTestTimer(
    CancellationAcknowledgementTestTimeProvider owner,
    TimerCallback callback,
    object? state) : ITimer
{
    private long? _dueTimestamp;
    private bool _disposed;

    public bool Change(TimeSpan dueTime, TimeSpan period) => owner.Change(this, dueTime, period);

    public void Dispose() => owner.Dispose(this);

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void Schedule(long? dueTimestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dueTimestamp = dueTimestamp;
    }

    internal bool TryTakeCallback(long timestamp, out TimerCallback dueCallback, out object? dueState)
    {
        if (_disposed || _dueTimestamp is null || timestamp < _dueTimestamp.Value)
        {
            dueCallback = null!;
            dueState = null;
            return false;
        }

        _dueTimestamp = null;
        dueCallback = callback;
        dueState = state;
        return true;
    }

    internal void MarkDisposed()
    {
        _disposed = true;
        _dueTimestamp = null;
    }
}
