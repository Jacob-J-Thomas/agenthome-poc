using System.Collections.Immutable;

namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Records one terminal fail-closed retirement of a wake before release or after an unrecoverable certainty block.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="RetirementId">The globally unique immutable retirement identity.</param>
/// <param name="Wake">The exact published wake reference.</param>
/// <param name="Reservation">The exact continuation reservation reference.</param>
/// <param name="ExpectedGeneration">The exact retired generation.</param>
/// <param name="Outcome">The closed non-completion retirement outcome.</param>
/// <param name="RetiredAtUtc">The trusted UTC retirement time.</param>
/// <param name="Evidence">The canonical ordered bounded redacted retirement evidence.</param>
/// <param name="Provenance">The immutable trusted coordinator provenance.</param>
/// <param name="RetirementHash">The canonical hash of every behavior-affecting retirement field.</param>
public sealed record HumanReviewContinuationRetirement(int SchemaVersion, string RetirementId, HumanReviewContinuationWakeReference Wake, HumanReviewContinuationReservationReference Reservation, long ExpectedGeneration, HumanReviewContinuationOutcome Outcome, DateTimeOffset RetiredAtUtc, ImmutableArray<HumanReviewRedactedPreview> Evidence, HumanReviewProvenance Provenance, string RetirementHash)
{
    /// <summary>Gets the only supported continuation-retirement schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;
}
