using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Names one exact Human Review decision or claimed continuation that must be re-read from canonical state before consumption.</summary>
/// <param name="Request">The immutable reviewed request reference.</param>
/// <param name="Decision">The accepted decision reference.</param>
/// <param name="Wake">The exact published wake when an approval has been published.</param>
/// <param name="Claim">The exact worker claim when a worker owns an approval continuation.</param>
/// <param name="Reservation">The exact approval reservation when an approval has been accepted.</param>
/// <param name="ExpectedGeneration">The claimed wake generation when a continuation is being consumed.</param>
public sealed record HumanReviewContinuationCandidateQuery(
    HumanReviewRequestReference Request,
    HumanReviewDecisionReference Decision,
    HumanReviewContinuationWakeReference? Wake,
    HumanReviewContinuationClaimReference? Claim,
    HumanReviewContinuationReservationReference? Reservation,
    long? ExpectedGeneration);
