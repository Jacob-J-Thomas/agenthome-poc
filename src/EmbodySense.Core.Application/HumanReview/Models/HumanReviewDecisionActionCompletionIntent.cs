using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Fences durable completion of one exact claimed non-approval decision action.</summary>
public sealed record HumanReviewDecisionActionCompletionIntent(string RunId, int ExpectedLifecycleVersion, HumanReviewDecisionActionWakeReference Wake, HumanReviewDecisionActionClaimReference Claim, HumanReviewDecisionActionReservationReference Reservation, long ExpectedGeneration);
