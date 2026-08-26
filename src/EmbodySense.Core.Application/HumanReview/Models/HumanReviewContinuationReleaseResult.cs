using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns the closed result of a host-neutral exact continuation release attempt.</summary>
/// <param name="Status">The closed release posture.</param>
/// <param name="Completion">The exact conclusive completion only when <paramref name="Status"/> is <see cref="HumanReviewContinuationReleaseStatus.Completed"/>.</param>
public sealed record HumanReviewContinuationReleaseResult(HumanReviewContinuationReleaseStatus Status, HumanReviewContinuationCompletion? Completion = null);
