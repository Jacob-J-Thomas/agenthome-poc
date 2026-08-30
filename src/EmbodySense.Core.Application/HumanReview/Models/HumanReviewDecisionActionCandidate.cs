using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns one exact reread canonical candidate with its claimed non-approval action state.</summary>
public sealed record HumanReviewDecisionActionCandidate(HumanReviewContinuationCandidate ConsumerCandidate, HumanReviewDecisionActionState Action, HumanReviewDecisionActionClaim Claim);
