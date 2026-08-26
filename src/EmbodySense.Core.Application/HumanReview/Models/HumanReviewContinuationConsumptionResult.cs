namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns one closed continuation-consumption disposition and at most one declared action or terminal intent.</summary>
/// <param name="Status">The closed Application disposition.</param>
/// <param name="Action">The declared non-terminal or release action, when one is safe.</param>
/// <param name="Completion">The exact claim completion precondition for a prepared release, when one is safe.</param>
/// <param name="Retirement">The terminal fail-closed retirement intent, when release is permanently blocked.</param>
public sealed record HumanReviewContinuationConsumptionResult(
    HumanReviewContinuationConsumptionStatus Status,
    HumanReviewContinuationActionIntent? Action = null,
    HumanReviewContinuationCompletionIntent? Completion = null,
    HumanReviewContinuationRetirementIntent? Retirement = null);
