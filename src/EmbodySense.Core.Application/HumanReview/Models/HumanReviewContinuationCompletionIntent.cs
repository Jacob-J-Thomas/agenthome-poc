using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Names the exact approved claim that a later canonical worker may complete after its declared action commits conclusively.</summary>
/// <param name="Wake">The exact published wake.</param>
/// <param name="Claim">The exact active worker claim.</param>
/// <param name="Reservation">The exact approved continuation reservation.</param>
/// <param name="ExpectedGeneration">The exact wake generation.</param>
public sealed record HumanReviewContinuationCompletionIntent(
    HumanReviewContinuationWakeReference Wake,
    HumanReviewContinuationClaimReference Claim,
    HumanReviewContinuationReservationReference Reservation,
    long ExpectedGeneration);
