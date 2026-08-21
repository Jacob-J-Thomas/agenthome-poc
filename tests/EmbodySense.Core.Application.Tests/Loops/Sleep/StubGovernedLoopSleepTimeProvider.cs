namespace EmbodySense.Core.Application.Tests.Loops.Sleep;

internal sealed class StubGovernedLoopSleepTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private int _callCount;

    internal DateTimeOffset UtcNow { get; set; } = utcNow;

    internal Exception? Exception { get; set; }

    internal int? ThrowOnCall { get; set; }

    internal int CallCount => _callCount;

    public override DateTimeOffset GetUtcNow()
    {
        _callCount++;
        if (Exception is not null)
        {
            throw Exception;
        }

        if (ThrowOnCall == _callCount)
        {
            throw new InvalidOperationException("simulated clock failure");
        }

        return UtcNow;
    }
}
