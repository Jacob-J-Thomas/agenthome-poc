using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Names one exact eligible accepted continuation discovered from canonical state before it is claimed.</summary>
/// <param name="RunId">The immutable canonical run identity.</param>
/// <param name="ExpectedLifecycleVersion">The observed whole-run version required for an atomic claim.</param>
/// <param name="Request">The exact accepted review request.</param>
/// <param name="Decision">The exact accepted approval decision.</param>
/// <param name="Wake">The exact published nonterminal wake.</param>
/// <param name="ExpectedGeneration">The immutable wake generation fenced by every claim and terminal artifact.</param>
/// <param name="WakeExpiresAtUtc">The immutable inclusive wake deadline; a recovery worker must not synthesize a new claim at or after this instant.</param>
/// <param name="Reservation">The exact accepted continuation reservation.</param>
/// <param name="PriorClaim">The latest strictly expired claim, or null when no claim has been appended.</param>
public sealed record HumanReviewContinuationRecoveryCandidate(
    string RunId,
    int ExpectedLifecycleVersion,
    HumanReviewRequestReference Request,
    HumanReviewDecisionReference Decision,
    HumanReviewContinuationWakeReference Wake,
    long ExpectedGeneration,
    DateTimeOffset WakeExpiresAtUtc,
    HumanReviewContinuationReservationReference Reservation,
    HumanReviewContinuationClaimReference? PriorClaim);
