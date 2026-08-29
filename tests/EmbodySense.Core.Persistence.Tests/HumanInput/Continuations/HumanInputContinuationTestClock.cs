namespace EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;

internal sealed class HumanInputContinuationTestClock(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
