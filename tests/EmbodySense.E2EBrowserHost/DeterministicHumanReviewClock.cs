using EmbodySense.Core.Application.HumanReview;

namespace EmbodySense.E2EBrowserHost;

internal sealed class DeterministicHumanReviewClock(DateTimeOffset utcNow) : TimeProvider, IHumanReviewTrustedClock
{
    public DateTimeOffset UtcNow { get; } = utcNow.Offset == TimeSpan.Zero
        ? utcNow
        : throw new ArgumentException("The deterministic Human Review clock must use UTC.", nameof(utcNow));

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
