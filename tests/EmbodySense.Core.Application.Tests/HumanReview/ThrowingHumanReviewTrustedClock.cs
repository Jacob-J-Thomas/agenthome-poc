using EmbodySense.Core.Application.HumanReview;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class ThrowingHumanReviewTrustedClock(Exception exception) : IHumanReviewTrustedClock
{
    public DateTimeOffset UtcNow => throw exception;
}
