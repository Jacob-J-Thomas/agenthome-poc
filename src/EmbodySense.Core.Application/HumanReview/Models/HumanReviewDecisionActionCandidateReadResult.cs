namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns a closed fenced reread result for one non-approval decision action.</summary>
public sealed record HumanReviewDecisionActionCandidateReadResult(HumanReviewDecisionActionCandidateReadStatus Status, HumanReviewDecisionActionCandidate? Candidate = null);
