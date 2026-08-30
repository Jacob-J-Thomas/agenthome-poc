namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns the outcome for one discovered decision action.</summary>
public sealed record HumanReviewDecisionActionRecoveryItemResult(HumanReviewDecisionActionRecoveryCandidate Candidate, HumanReviewDecisionActionRecoveryItemStatus Status);
