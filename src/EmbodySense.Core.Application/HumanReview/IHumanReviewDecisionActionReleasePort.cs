using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Executes one already-claimed non-approval action without becoming an authority source.</summary>
/// <remarks>Implementations must be idempotent by <see cref="HumanReviewDecisionActionIntent.ActionOperationId"/>: retries and strict-expiry claim takeovers for the same reservation use that exact key and must not create a second external action. Implementations must not infer approval or effect-release authority from this non-approval intent.</remarks>
public interface IHumanReviewDecisionActionReleasePort
{
    /// <summary>Applies the exact prepared action only after canonical reread confirms its current claim.</summary>
    /// <param name="intent">The exact current non-approval action intent, including its durable idempotency key.</param>
    /// <param name="cancellationToken">Cancels only before a conclusive result is returned; implementations must preserve idempotent reconciliation for response-unknown outcomes.</param>
    /// <returns>A conclusive completion, invalid evidence result, or unavailable result for the exact operation key.</returns>
    Task<HumanReviewDecisionActionReleaseResult> ReleaseAsync(HumanReviewDecisionActionIntent intent, CancellationToken cancellationToken = default);
}
