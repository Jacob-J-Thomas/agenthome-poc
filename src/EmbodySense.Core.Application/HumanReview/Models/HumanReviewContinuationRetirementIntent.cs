using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Requests one terminal fail-closed retirement of the exact approved wake without constructing or persisting the durable artifact.</summary>
/// <param name="Wake">The exact published wake.</param>
/// <param name="Reservation">The exact approved continuation reservation.</param>
/// <param name="ExpectedGeneration">The exact wake generation.</param>
/// <param name="Outcome">The closed non-completion outcome required by strict continuation contracts.</param>
/// <param name="Reason">The bounded Application reason for the retirement.</param>
public sealed record HumanReviewContinuationRetirementIntent(
    HumanReviewContinuationWakeReference Wake,
    HumanReviewContinuationReservationReference Reservation,
    long ExpectedGeneration,
    HumanReviewContinuationOutcome Outcome,
    HumanReviewContinuationRetirementReason Reason);
