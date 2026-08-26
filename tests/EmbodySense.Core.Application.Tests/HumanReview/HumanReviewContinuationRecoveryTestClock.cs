using EmbodySense.Core.Application.HumanReview;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewContinuationRecoveryTestClock(DateTimeOffset now, Exception? exception = null) : IHumanReviewTrustedClock
{
    public DateTimeOffset UtcNow => exception is null ? now : throw exception;
}
