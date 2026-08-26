using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Builds a validated terminal continuation completion from a pre-bound Application receipt intent and conclusive worker evidence.</summary>
/// <remarks>This pure factory does not persist, claim, release, or dispatch work. A later durable worker must re-read its canonical request, wake, reservation, and claim, obtain conclusive result and frontier evidence, and compare-exchange the returned completion exactly once.</remarks>
public static class HumanReviewContinuationCompletionIntentFactory
{
    /// <summary>Creates a strict schema-1 completion only when the re-read canonical context exactly matches the pre-bound release intent.</summary>
    /// <param name="intent">The Application precondition emitted before the release boundary.</param>
    /// <param name="request">The current canonical Human Review request.</param>
    /// <param name="wake">The current canonical published wake.</param>
    /// <param name="reservation">The current canonical accepted continuation reservation.</param>
    /// <param name="claim">The current canonical worker claim.</param>
    /// <param name="completionId">The unique durable completion identity assigned after the release concludes.</param>
    /// <param name="resultHash">The canonical conclusive governed-release result hash.</param>
    /// <param name="frontierReceiptHash">The canonical persisted frontier-receipt hash.</param>
    /// <param name="completedAtUtc">The trusted UTC instant at which the release concluded.</param>
    /// <param name="evidence">The bounded, canonical, redacted completion evidence.</param>
    /// <param name="provenance">The canonical coordinator provenance observed at <paramref name="completedAtUtc"/>.</param>
    /// <param name="completion">The exact canonical completion when validation succeeds; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> only when the request-bound release receipt and completion validate exactly.</returns>
    public static bool TryCreate(
        HumanReviewContinuationCompletionIntent? intent,
        HumanReviewRequest? request,
        HumanReviewContinuationWake? wake,
        HumanReviewContinuationReservation? reservation,
        HumanReviewContinuationClaim? claim,
        string? completionId,
        string? resultHash,
        string? frontierReceiptHash,
        DateTimeOffset completedAtUtc,
        ImmutableArray<HumanReviewRedactedPreview> evidence,
        HumanReviewProvenance? provenance,
        out HumanReviewContinuationCompletion? completion)
    {
        completion = null;
        try
        {
            if (!Matches(intent, request, wake, reservation, claim) || intent is null || request is null || wake is null || reservation is null || claim is null
                || !HumanReviewContinuationContractValidator.ValidateWake(request, reservation, wake).IsValid
                || !HumanReviewContinuationContractValidator.ValidateClaim(wake, reservation, claim).IsValid)
            {
                return false;
            }

            var receiptIntent = intent.ReleaseReceipt;
            var receipt = HumanReviewContinuationContractHash.ApplyReleaseReceipt(new HumanReviewContinuationReleaseReceipt(
                HumanReviewContinuationReleaseReceipt.CurrentSchemaVersion,
                receiptIntent.ReleaseOperationId,
                receiptIntent.Wake,
                receiptIntent.Claim,
                receiptIntent.Reservation,
                receiptIntent.ExpectedGeneration,
                receiptIntent.Kind,
                HumanReviewContinuationReleaseDisposition.Released,
                resultHash!,
                frontierReceiptHash!,
                receiptIntent.EffectReceiptHash,
                string.Empty));
            if (!HumanReviewContinuationContractValidator.ValidateReleaseReceipt(request, wake, reservation, claim, receipt).IsValid)
            {
                return false;
            }

            var candidate = HumanReviewContinuationContractHash.ApplyCompletion(new HumanReviewContinuationCompletion(
                HumanReviewContinuationCompletion.CurrentSchemaVersion,
                completionId!,
                intent.Wake,
                intent.Claim,
                intent.Reservation,
                intent.ExpectedGeneration,
                receipt,
                completedAtUtc,
                evidence,
                provenance!,
                string.Empty));
            if (!HumanReviewContinuationContractValidator.ValidateCompletion(request, wake, reservation, claim, candidate).IsValid)
            {
                return false;
            }

            completion = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NullReferenceException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    private static bool Matches(
        HumanReviewContinuationCompletionIntent? intent,
        HumanReviewRequest? request,
        HumanReviewContinuationWake? wake,
        HumanReviewContinuationReservation? reservation,
        HumanReviewContinuationClaim? claim)
    {
        if (intent is null || request is null || wake is null || reservation is null || claim is null || intent.ReleaseReceipt is null)
        {
            return false;
        }

        var receipt = intent.ReleaseReceipt;
        return string.Equals(intent.Request.RequestId, request.RequestId, StringComparison.Ordinal)
            && string.Equals(intent.Request.RequestHash, request.RequestHash, StringComparison.Ordinal)
            && Equals(intent.Wake, new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash))
            && Equals(intent.Claim, new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash))
            && Equals(intent.Reservation, new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash))
            && intent.ExpectedGeneration == wake.ExpectedGeneration
            && Equals(receipt.Request, intent.Request)
            && Equals(receipt.Wake, intent.Wake)
            && Equals(receipt.Claim, intent.Claim)
            && Equals(receipt.Reservation, intent.Reservation)
            && receipt.ExpectedGeneration == intent.ExpectedGeneration;
    }
}
