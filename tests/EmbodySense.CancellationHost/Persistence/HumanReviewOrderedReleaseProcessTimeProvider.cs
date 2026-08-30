namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessTimeProvider(DateTimeOffset initialUtc) : TimeProvider
{
    private long _ticks = initialUtc.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Increment(ref _ticks), TimeSpan.Zero);
}
