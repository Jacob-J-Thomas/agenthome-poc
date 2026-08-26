namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns one closed canonical completion or retirement mutation result without exposing mutable store state.</summary>
/// <param name="Status">The closed store-mutation posture.</param>
public sealed record HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus Status);
