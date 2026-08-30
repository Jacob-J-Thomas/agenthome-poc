using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Fences one fail-closed retirement of an exact action chain.</summary>
public sealed record HumanReviewDecisionActionRetirementIntent(string RunId, int ExpectedLifecycleVersion, HumanReviewDecisionActionWakeReference Wake, HumanReviewDecisionActionClaimReference? Claim, HumanReviewDecisionActionReservationReference Reservation, long ExpectedGeneration, HumanReviewContinuationOutcome Outcome, HumanReviewDecisionActionRetirementReason Reason);
