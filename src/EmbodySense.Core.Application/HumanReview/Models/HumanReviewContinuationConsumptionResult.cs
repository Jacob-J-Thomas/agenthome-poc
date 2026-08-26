namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns one closed continuation-consumption disposition and at most one declared action or terminal intent.</summary>
/// <param name="Status">The closed Application disposition.</param>
/// <param name="Action">The declared non-terminal or release action, when one is safe.</param>
/// <param name="Completion">The exact post-action completion precondition for a prepared release; it is not evidence that the action committed.</param>
/// <param name="Retirement">The terminal fail-closed claim- and lifecycle-fenced retirement intent, when release is permanently blocked.</param>
public sealed record HumanReviewContinuationConsumptionResult(
    HumanReviewContinuationConsumptionStatus Status,
    HumanReviewContinuationActionIntent? Action = null,
    HumanReviewContinuationCompletionIntent? Completion = null,
    HumanReviewContinuationRetirementIntent? Retirement = null);
