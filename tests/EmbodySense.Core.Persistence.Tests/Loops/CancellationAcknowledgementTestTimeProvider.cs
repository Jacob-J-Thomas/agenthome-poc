namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class CancellationAcknowledgementTestTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly HashSet<CancellationAcknowledgementTestTimer> _timers = [];
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        RequireOneShotPeriod(period);
        var timer = new CancellationAcknowledgementTestTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            Schedule(timer, dueTime);
        }

        return timer;
    }

    internal void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_gate)
        {
            _timestamp = checked(_timestamp + elapsed.Ticks);
            _utcNow = _utcNow.Add(elapsed);
            foreach (var timer in _timers)
            {
                if (timer.TryTakeCallback(_timestamp, out var callback, out var state))
                {
                    callbacks.Add((callback, state));
                }
            }
        }

        foreach (var callback in callbacks)
        {
            callback.Callback(callback.State);
        }
    }

    internal bool Change(CancellationAcknowledgementTestTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        RequireOneShotPeriod(period);
        lock (_gate)
        {
            if (!_timers.Contains(timer))
            {
                return false;
            }

            Schedule(timer, dueTime);
            return true;
        }
    }

    internal void Dispose(CancellationAcknowledgementTestTimer timer)
    {
        lock (_gate)
        {
            if (_timers.Remove(timer))
            {
                timer.MarkDisposed();
            }
        }
    }

    private void Schedule(CancellationAcknowledgementTestTimer timer, TimeSpan dueTime)
    {
        if (dueTime == Timeout.InfiniteTimeSpan)
        {
            timer.Schedule(null);
            return;
        }

        if (dueTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(dueTime));
        }

        timer.Schedule(checked(_timestamp + dueTime.Ticks));
    }

    private static void RequireOneShotPeriod(TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "The acknowledgement test clock supports one-shot timers only.");
        }
    }
}
