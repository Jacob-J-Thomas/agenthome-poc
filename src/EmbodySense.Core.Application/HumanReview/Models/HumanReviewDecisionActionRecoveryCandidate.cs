using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Describes one strictly claimable published non-approval action from canonical state.</summary>
public sealed record HumanReviewDecisionActionRecoveryCandidate(string RunId, int ExpectedLifecycleVersion, HumanReviewRequestReference Request, HumanReviewDecisionReference Decision, HumanReviewDecisionActionWakeReference Wake, long ExpectedGeneration, DateTimeOffset WakeExpiresAtUtc, HumanReviewDecisionActionReservationReference Reservation, HumanReviewDecisionActionClaimReference? PriorClaim);
