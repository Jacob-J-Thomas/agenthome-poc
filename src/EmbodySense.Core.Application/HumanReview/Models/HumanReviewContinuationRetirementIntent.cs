using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Requests one terminal fail-closed retirement of the exact approved wake without constructing or persisting the durable artifact.</summary>
/// <param name="RunId">The exact durable run identity whose continuation state may be atomically retired.</param>
/// <param name="ExpectedLifecycleVersion">The exact current run lifecycle version that must still match before recording retirement.</param>
/// <param name="Wake">The exact published wake.</param>
/// <param name="Claim">The exact evaluated active claim that a store must fence before retirement.</param>
/// <param name="Reservation">The exact approved continuation reservation.</param>
/// <param name="ExpectedGeneration">The exact wake generation.</param>
/// <param name="Outcome">The closed non-completion outcome required by strict continuation contracts.</param>
/// <param name="Reason">The bounded Application reason for the retirement.</param>
public sealed record HumanReviewContinuationRetirementIntent(
    string RunId,
    int ExpectedLifecycleVersion,
    HumanReviewContinuationWakeReference Wake,
    HumanReviewContinuationClaimReference Claim,
    HumanReviewContinuationReservationReference Reservation,
    long ExpectedGeneration,
    HumanReviewContinuationOutcome Outcome,
    HumanReviewContinuationRetirementReason Reason);
