namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class ImmediateTimerTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private int _timerCount;

    internal int TimerCount => Volatile.Read(ref _timerCount);

    public override DateTimeOffset GetUtcNow() => utcNow;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(20), dueTime);
        Assert.Equal(Timeout.InfiniteTimeSpan, period);
        Interlocked.Increment(ref _timerCount);
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return NoopTimer.Instance;
    }

    private sealed class NoopTimer : ITimer
    {
        internal static NoopTimer Instance { get; } = new();

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
