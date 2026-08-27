using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Executes one already-prepared Human Review continuation release without becoming an authority source or worker host.</summary>
/// <remarks>The implementation must bind idempotency to <see cref="HumanReviewContinuationActionIntent.ReleaseReceipt"/> and return a completion only after conclusive durable release evidence exists. Ambiguous or unavailable results must not be converted into redispatch.</remarks>
public interface IHumanReviewContinuationReleasePort
{
    /// <summary>Attempts one exact prepared release after the recovery coordinator has acquired and reread its current claim.</summary>
    /// <param name="action">The exact Application-prepared action.</param>
    /// <param name="completion">The exact post-release completion precondition.</param>
    /// <param name="cancellationToken">Cancels the invocation before a result is available.</param>
    /// <returns>A conclusive completion, unavailable, ambiguous, or invalid closed result.</returns>
    Task<HumanReviewContinuationReleaseResult> ReleaseAsync(HumanReviewContinuationActionIntent action, HumanReviewContinuationCompletionIntent completion, CancellationToken cancellationToken = default);
}
