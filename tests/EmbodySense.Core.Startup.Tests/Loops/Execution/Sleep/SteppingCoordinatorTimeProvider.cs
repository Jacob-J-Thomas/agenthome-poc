namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class SteppingCoordinatorTimeProvider(DateTimeOffset utcNow, TimeSpan step) : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _utcNow = utcNow;

    internal bool ThrowOnNext { get; set; }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            if (ThrowOnNext)
            {
                ThrowOnNext = false;
                throw new InvalidOperationException("hostile clock failure");
            }

            var current = _utcNow;
            _utcNow = _utcNow.Add(step);
            return current;
        }
    }

    internal void Advance(TimeSpan duration)
    {
        lock (_gate)
        {
            _utcNow = _utcNow.Add(duration);
        }
    }
}
