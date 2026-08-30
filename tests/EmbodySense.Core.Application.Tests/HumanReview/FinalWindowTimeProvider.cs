namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class FinalWindowTimeProvider(DateTimeOffset initialValue) : TimeProvider
{
    public DateTimeOffset CurrentValue { get; set; } = initialValue;

    public int ReadCount { get; private set; }

    public override DateTimeOffset GetUtcNow()
    {
        ReadCount++;
        return CurrentValue;
    }
}
