using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Describes one exact claimed non-approval action for a host-owned action adapter.</summary>
/// <remarks>This intent is non-authorizing and contains no approval or effect-release evidence.</remarks>
public sealed record HumanReviewDecisionActionIntent(string RunId, int ExpectedLifecycleVersion, HumanReviewRequestReference Request, HumanReviewDecisionReference Decision, HumanReviewDecisionActionWakeReference Wake, HumanReviewDecisionActionClaimReference Claim, HumanReviewDecisionActionReservationReference Reservation, long ExpectedGeneration, string ActionOperationId);
