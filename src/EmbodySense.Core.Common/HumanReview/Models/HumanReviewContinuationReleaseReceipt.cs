namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Retains the stable, preexisting governed-release receipt that lets a response-lost completion be reconciled without repeating release.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="ReleaseOperationId">The preexisting stable operation identity assigned before the governed release boundary is crossed.</param>
/// <param name="Wake">The exact published wake reference bound to the release operation.</param>
/// <param name="Claim">The exact active claim reference bound to the release operation.</param>
/// <param name="Reservation">The exact continuation reservation reference bound to the release operation.</param>
/// <param name="ExpectedGeneration">The exact wake generation bound to the release operation.</param>
/// <param name="Kind">The continuation or pre-dispatch effect boundary that produced this receipt.</param>
/// <param name="Disposition">The closed conclusive release result; only <see cref="HumanReviewContinuationReleaseDisposition.Released"/> supports completion.</param>
/// <param name="ResultHash">The canonical hash of the governed release result.</param>
/// <param name="FrontierReceiptHash">The canonical hash of the exact persisted frontier receipt.</param>
/// <param name="EffectReceiptHash">The canonical effect receipt hash required for a pre-dispatch effect release and absent for a continuation-only release.</param>
/// <param name="ReleaseReceiptHash">The canonical hash of every behavior-affecting release-receipt field.</param>
public sealed record HumanReviewContinuationReleaseReceipt(
    int SchemaVersion,
    string ReleaseOperationId,
    HumanReviewContinuationWakeReference Wake,
    HumanReviewContinuationClaimReference Claim,
    HumanReviewContinuationReservationReference Reservation,
    long ExpectedGeneration,
    HumanReviewContinuationReleaseKind Kind,
    HumanReviewContinuationReleaseDisposition Disposition,
    string ResultHash,
    string FrontierReceiptHash,
    string? EffectReceiptHash,
    string ReleaseReceiptHash)
{
    /// <summary>Gets the only supported continuation-release-receipt schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;
}
