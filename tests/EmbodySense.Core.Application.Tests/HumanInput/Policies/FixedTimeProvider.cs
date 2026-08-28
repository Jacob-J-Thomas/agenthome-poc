namespace EmbodySense.Core.Application.Tests.HumanInput.Policies;

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
