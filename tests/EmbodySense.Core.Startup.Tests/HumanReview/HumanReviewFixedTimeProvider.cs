namespace EmbodySense.Core.Startup.Tests.HumanReview;

internal sealed class HumanReviewFixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
