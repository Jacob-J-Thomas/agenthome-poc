namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanInputResponseContinuationThrowingTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("clock unavailable");
}
