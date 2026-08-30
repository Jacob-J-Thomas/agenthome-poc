namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies one immutable lease claim for a non-approval decision action.</summary>
/// <param name="ClaimId">The claim identity.</param>
/// <param name="ClaimHash">The canonical hash of the claim.</param>
public sealed record HumanReviewDecisionActionClaimReference(string ClaimId, string ClaimHash);
