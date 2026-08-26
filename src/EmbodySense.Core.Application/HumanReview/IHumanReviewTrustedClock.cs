namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Supplies trusted UTC time for durable Human Review decisions.</summary>
public interface IHumanReviewTrustedClock
{
    /// <summary>Gets the current trusted UTC instant.</summary>
    DateTimeOffset UtcNow { get; }
}
