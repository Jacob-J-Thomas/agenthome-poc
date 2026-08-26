using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Requests one exact declared decision path after all Application revalidation succeeds; it is not a callback or authority grant.</summary>
/// <param name="Action">The only declared path that may be applied.</param>
/// <param name="RunId">The exact durable run identity.</param>
/// <param name="ExpectedLifecycleVersion">The current canonical run version that a later atomic mutation must compare-exchange.</param>
/// <param name="Request">The exact immutable review request.</param>
/// <param name="Decision">The exact accepted decision.</param>
/// <param name="Wake">The published approval wake for a release action, otherwise null.</param>
/// <param name="Claim">The current exact worker claim for a release action, otherwise null.</param>
/// <param name="Reservation">The exact approval reservation for a release action, otherwise null.</param>
/// <param name="ExpectedGeneration">The exact wake generation for a release action, otherwise null.</param>
/// <param name="EffectQuery">The exact effect certainty expectation for an effect release, otherwise null.</param>
public sealed record HumanReviewContinuationActionIntent(
    HumanReviewContinuationAction Action,
    string RunId,
    int ExpectedLifecycleVersion,
    HumanReviewRequestReference Request,
    HumanReviewDecisionReference Decision,
    HumanReviewContinuationWakeReference? Wake,
    HumanReviewContinuationClaimReference? Claim,
    HumanReviewContinuationReservationReference? Reservation,
    long? ExpectedGeneration,
    GovernedLoopEffectCertaintySnapshotQuery? EffectQuery);
