using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Reserves the immutable portion of one future governed release receipt before its irreversible boundary is crossed.</summary>
/// <remarks>A later durable worker must bind this preparation to its conclusive result and persisted frontier receipt before constructing <see cref="HumanReviewContinuationReleaseReceipt"/>. This intent is not a completed receipt, dispatch permission, or evidence that a release occurred.</remarks>
/// <param name="ReleaseOperationId">The deterministic stable release-operation identity allocated before the governed boundary.</param>
/// <param name="Request">The exact reviewed request that determines the only permitted release kind.</param>
/// <param name="Wake">The exact published continuation wake.</param>
/// <param name="Claim">The exact active worker claim.</param>
/// <param name="Reservation">The exact accepted continuation reservation.</param>
/// <param name="ExpectedGeneration">The exact wake generation.</param>
/// <param name="Kind">The exact continuation or pre-dispatch-effect boundary required by <paramref name="Request"/>.</param>
/// <param name="EffectReceiptHash">The final exact not-started certainty-snapshot hash for a pre-dispatch effect, or <see langword="null"/> for a continuation-only release.</param>
public sealed record HumanReviewContinuationReleaseReceiptIntent(
    string ReleaseOperationId,
    HumanReviewRequestReference Request,
    HumanReviewContinuationWakeReference Wake,
    HumanReviewContinuationClaimReference Claim,
    HumanReviewContinuationReservationReference Reservation,
    long ExpectedGeneration,
    HumanReviewContinuationReleaseKind Kind,
    string? EffectReceiptHash);
