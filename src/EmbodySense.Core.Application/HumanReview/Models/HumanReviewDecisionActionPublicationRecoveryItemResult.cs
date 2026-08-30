namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns the result of reconciling one retained wake-less decision-action reservation.</summary>
/// <param name="Candidate">The exact retained reservation considered by the bounded pass.</param>
/// <param name="Status">The canonical publication reconciliation result.</param>
public sealed record HumanReviewDecisionActionPublicationRecoveryItemResult(HumanReviewDecisionActionPublicationCandidate Candidate, HumanReviewDecisionActionPublicationRecoveryItemStatus Status);
