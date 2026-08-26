namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Records one bounded lease claim for a continuation wake; a claim is ownership evidence, never execution authority.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="ClaimId">The globally unique immutable claim identity.</param>
/// <param name="Wake">The exact published wake reference.</param>
/// <param name="Reservation">The exact continuation reservation reference.</param>
/// <param name="ExpectedGeneration">The exact claimed generation.</param>
/// <param name="WorkerId">The canonical durable worker identity.</param>
/// <param name="ClaimedAtUtc">The trusted UTC claim time.</param>
/// <param name="LeaseExpiresAtUtc">The inclusive trusted UTC lease expiry.</param>
/// <param name="Provenance">The immutable trusted coordinator provenance.</param>
/// <param name="ClaimHash">The canonical hash of every behavior-affecting claim field.</param>
public sealed record HumanReviewContinuationClaim(int SchemaVersion, string ClaimId, HumanReviewContinuationWakeReference Wake, HumanReviewContinuationReservationReference Reservation, long ExpectedGeneration, string WorkerId, DateTimeOffset ClaimedAtUtc, DateTimeOffset LeaseExpiresAtUtc, HumanReviewProvenance Provenance, string ClaimHash)
{
    /// <summary>Gets the only supported continuation-claim schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;
}
