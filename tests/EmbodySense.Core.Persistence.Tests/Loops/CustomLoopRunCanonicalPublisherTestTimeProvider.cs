namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class CustomLoopRunCanonicalPublisherTestTimeProvider(DateTimeOffset origin) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<CustomLoopRunCanonicalPublisherTestTimer> _timers = [];
    private readonly List<TimeSpan> _requestedDelays = [];
    private TaskCompletionSource<bool> _timerCreated = CreateSignal();
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    internal int TimerCount
    {
        get
        {
            lock (_gate)
            {
                return _requestedDelays.Count;
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return origin.AddTicks(_timestamp);
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
        ValidateDelay(dueTime);
        if (period != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "The canonical-publisher test clock supports one-shot timers only.");
        }

        TaskCompletionSource<bool> timerCreated;
        CustomLoopRunCanonicalPublisherTestTimer timer;
        lock (_gate)
        {
            var dueTimestamp = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(_timestamp + dueTime.Ticks);
            timer = new CustomLoopRunCanonicalPublisherTestTimer(this, callback, state, dueTimestamp);
            _timers.Add(timer);
            _requestedDelays.Add(dueTime);
            timerCreated = _timerCreated;
            _timerCreated = CreateSignal();
        }

        timerCreated.TrySetResult(true);
        return timer;
    }

    internal TimeSpan GetRequestedDelay(int ordinal)
    {
        lock (_gate)
        {
            return _requestedDelays[ordinal - 1];
        }
    }

    internal async Task WaitForTimerCountAsync(int count)
    {
        while (true)
        {
            Task signal;
            lock (_gate)
            {
                if (_requestedDelays.Count >= count)
                {
                    return;
                }

                signal = _timerCreated.Task;
            }

            await signal.ConfigureAwait(false);
        }
    }

    internal void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "The semantic test clock cannot move backwards.");
        }

        CustomLoopRunCanonicalPublisherTestTimer[] due;
        long timestamp;
        lock (_gate)
        {
            _timestamp = checked(_timestamp + duration.Ticks);
            timestamp = _timestamp;
            due = _timers.Where(timer => timer.IsDue(timestamp)).ToArray();
        }

        foreach (var timer in due)
        {
            timer.Fire(timestamp);
        }
    }

    internal bool Change(CustomLoopRunCanonicalPublisherTestTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        ValidateDelay(dueTime);
        if (period != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "The canonical-publisher test clock supports one-shot timers only.");
        }

        lock (_gate)
        {
            var dueTimestamp = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(_timestamp + dueTime.Ticks);
            return timer.ChangeCore(dueTimestamp);
        }
    }

    private static TaskCompletionSource<bool> CreateSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void ValidateDelay(TimeSpan dueTime)
    {
        if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(dueTime), dueTime, "The timer delay must be non-negative or infinite.");
        }
    }
}
