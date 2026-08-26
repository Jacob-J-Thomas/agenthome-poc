using EmbodySense.Core.Application.HumanReview;

namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class HumanReviewDecisionStoreTestClock(DateTimeOffset utcNow) : IHumanReviewTrustedClock
{
    public DateTimeOffset UtcNow => utcNow;
}
