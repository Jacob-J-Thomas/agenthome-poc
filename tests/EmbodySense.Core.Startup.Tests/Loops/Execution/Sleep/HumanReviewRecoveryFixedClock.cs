using EmbodySense.Core.Application.HumanReview;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanReviewRecoveryFixedClock(DateTimeOffset now) : IHumanReviewTrustedClock
{
    public DateTimeOffset UtcNow => now;
}
