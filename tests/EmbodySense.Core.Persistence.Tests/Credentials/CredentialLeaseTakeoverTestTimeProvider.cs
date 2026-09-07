namespace EmbodySense.Core.Persistence.Tests.Credentials;

internal sealed class CredentialLeaseTakeoverTestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<CredentialLeaseTakeoverTestTimer> _timers = [];
    private TaskCompletionSource _timerCreated = CreateSignal();
    private DateTimeOffset _utcNow = utcNow;
    private long _timestamp;
    private int _timerCreationCount;

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
        ValidateTimeout(dueTime, nameof(dueTime));
        ValidateTimeout(period, nameof(period));
        lock (_gate)
        {
            var timer = new CredentialLeaseTakeoverTestTimer(this, callback, state);
            _timers.Add(timer);
            ChangeCore(timer, dueTime, period);
            _timerCreationCount++;
            _timerCreated.TrySetResult();
            _timerCreated = CreateSignal();
            return timer;
        }
    }

    internal async Task WaitForTimerCreationsAsync(int count)
    {
        while (true)
        {
            Task pending;
            lock (_gate)
            {
                if (_timerCreationCount >= count)
                {
                    return;
                }
                pending = _timerCreated.Task;
            }
            await pending.ConfigureAwait(false);
        }
    }

    internal void Advance(TimeSpan duration) => AdvanceCore(duration, fireTimers: true);

    internal void AdvanceClockWithoutFiringTimers(TimeSpan duration) => AdvanceCore(duration, fireTimers: false);

    internal bool Change(CredentialLeaseTakeoverTestTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        ValidateTimeout(dueTime, nameof(dueTime));
        ValidateTimeout(period, nameof(period));
        lock (_gate)
        {
            return ChangeCore(timer, dueTime, period);
        }
    }

    internal void Dispose(CredentialLeaseTakeoverTestTimer timer)
    {
        lock (_gate)
        {
            timer.DisposeCore();
        }
    }

    private void AdvanceCore(TimeSpan duration, bool fireTimers)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        List<(TimerCallback Callback, object? State)> due = [];
        lock (_gate)
        {
            _utcNow += duration;
            _timestamp = checked(_timestamp + duration.Ticks);
            if (fireTimers)
            {
                foreach (var timer in _timers)
                {
                    if (timer.TryFireCore(_timestamp, out var callback, out var state))
                    {
                        due.Add((callback!, state));
                    }
                }
            }
        }

        foreach (var invocation in due)
        {
            invocation.Callback(invocation.State);
        }
    }

    private bool ChangeCore(CredentialLeaseTakeoverTestTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        var dueAtTimestamp = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(_timestamp + dueTime.Ticks);
        return timer.ChangeCore(dueAtTimestamp, period);
    }

    private static TaskCompletionSource CreateSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
