namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies one immutable continuation claim by its exact canonical hash.</summary>
/// <param name="ClaimId">The globally unique claim identity.</param>
/// <param name="ClaimHash">The exact canonical claim hash.</param>
public sealed record HumanReviewContinuationClaimReference(string ClaimId, string ClaimHash);
