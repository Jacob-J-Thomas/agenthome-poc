using EmbodySense.Core.Application.HumanReview;

namespace EmbodySense.E2ETests.Web;

internal sealed class HumanReviewBrowserFixtureTrustedClock(DateTimeOffset now) : IHumanReviewTrustedClock
{
    public DateTimeOffset UtcNow => now;
}
