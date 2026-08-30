using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Fences one exact append-only claim of a strict-expiry eligible non-approval decision action.</summary>
public sealed record HumanReviewDecisionActionClaimIntent(HumanReviewDecisionActionRecoveryCandidate Candidate, HumanReviewDecisionActionClaim Claim);
