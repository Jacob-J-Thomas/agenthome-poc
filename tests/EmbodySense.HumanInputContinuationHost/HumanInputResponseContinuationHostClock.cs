namespace EmbodySense.HumanInputContinuationHost;

internal sealed class HumanInputResponseContinuationHostClock(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
