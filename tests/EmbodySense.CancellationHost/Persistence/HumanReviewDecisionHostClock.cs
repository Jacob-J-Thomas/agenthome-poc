using EmbodySense.Core.Application.HumanReview;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewDecisionHostClock(DateTimeOffset utcNow) : IHumanReviewTrustedClock
{
    public DateTimeOffset UtcNow => utcNow;
}
