using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Names the exact post-action completion precondition that a later canonical worker may compare-exchange after its declared action commits conclusively.</summary>
/// <param name="RunId">The exact durable run identity whose continuation state may be atomically completed.</param>
/// <param name="ExpectedLifecycleVersion">The exact current run lifecycle version that must still match before recording completion.</param>
/// <param name="Wake">The exact published wake.</param>
/// <param name="Claim">The exact active worker claim.</param>
/// <param name="Reservation">The exact approved continuation reservation.</param>
/// <param name="ExpectedGeneration">The exact wake generation.</param>
public sealed record HumanReviewContinuationCompletionIntent(
    string RunId,
    int ExpectedLifecycleVersion,
    HumanReviewContinuationWakeReference Wake,
    HumanReviewContinuationClaimReference Claim,
    HumanReviewContinuationReservationReference Reservation,
    long ExpectedGeneration);
