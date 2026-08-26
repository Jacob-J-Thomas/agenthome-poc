using System.Collections.Immutable;

namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Records terminal completion of one exact active claim without asserting that consent supplied current authority.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="CompletionId">The globally unique immutable completion identity.</param>
/// <param name="Wake">The exact published wake reference.</param>
/// <param name="Claim">The exact active claim reference.</param>
/// <param name="Reservation">The exact continuation reservation reference.</param>
/// <param name="ExpectedGeneration">The exact completed generation.</param>
/// <param name="ReleaseReceipt">The exact preexisting governed-release receipt that proves response loss can be reconciled without redispatch.</param>
/// <param name="CompletedAtUtc">The trusted UTC completion time.</param>
/// <param name="Evidence">The canonical ordered bounded redacted completion evidence.</param>
/// <param name="Provenance">The immutable trusted coordinator provenance.</param>
/// <param name="CompletionHash">The canonical hash of every behavior-affecting completion field.</param>
public sealed record HumanReviewContinuationCompletion(int SchemaVersion, string CompletionId, HumanReviewContinuationWakeReference Wake, HumanReviewContinuationClaimReference Claim, HumanReviewContinuationReservationReference Reservation, long ExpectedGeneration, HumanReviewContinuationReleaseReceipt ReleaseReceipt, DateTimeOffset CompletedAtUtc, ImmutableArray<HumanReviewRedactedPreview> Evidence, HumanReviewProvenance Provenance, string CompletionHash)
{
    /// <summary>Gets the only supported continuation-completion schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;
}
