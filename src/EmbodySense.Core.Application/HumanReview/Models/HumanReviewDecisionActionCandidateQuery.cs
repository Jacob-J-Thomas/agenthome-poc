using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Fences one exact post-claim reread of a retained non-approval decision action.</summary>
public sealed record HumanReviewDecisionActionCandidateQuery(string RunId, HumanReviewRequestReference Request, HumanReviewDecisionReference Decision, HumanReviewDecisionActionWakeReference Wake, HumanReviewDecisionActionClaimReference Claim, HumanReviewDecisionActionReservationReference Reservation, long ExpectedGeneration);
