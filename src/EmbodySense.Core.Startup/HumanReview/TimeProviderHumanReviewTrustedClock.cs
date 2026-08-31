using EmbodySense.Core.Application.HumanReview;

namespace EmbodySense.Core.Startup.HumanReview;

/// <summary>Adapts a hosting <see cref="TimeProvider"/> to the trusted UTC clock required by Human Review services.</summary>
/// <remarks>The adapter preserves UTC and leaves provider failures visible to the Application service, which then fails closed as
/// unavailable. It never accepts a timestamp from a surface request.</remarks>
public sealed class TimeProviderHumanReviewTrustedClock : IHumanReviewTrustedClock
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the trusted-clock adapter.</summary>
    /// <param name="timeProvider">The server-owned time provider.</param>
    public TimeProviderHumanReviewTrustedClock(TimeProvider timeProvider)
        => _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow().ToUniversalTime();
}
