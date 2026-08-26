namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns one closed current-authority posture without granting or reserving authority.</summary>
/// <param name="Status">The closed independently revalidated posture.</param>
public sealed record HumanReviewContinuationAuthorityReadResult(HumanReviewContinuationAuthorityReadStatus Status);
