namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns one closed canonical continuation mutation posture without exposing mutable state.</summary>
/// <param name="Status">The closed mutation result.</param>
public sealed record HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus Status);
