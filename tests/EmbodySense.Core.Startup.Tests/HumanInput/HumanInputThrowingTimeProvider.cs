namespace EmbodySense.Core.Startup.Tests.HumanInput;

internal sealed class HumanInputThrowingTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("clock unavailable");
}
