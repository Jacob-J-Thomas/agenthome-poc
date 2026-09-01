using EmbodySense.Core.Application.HumanReview;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanReviewRecoveryTrustedClock(DateTimeOffset now) : IHumanReviewTrustedClock
{
    public DateTimeOffset UtcNow => now;
}
