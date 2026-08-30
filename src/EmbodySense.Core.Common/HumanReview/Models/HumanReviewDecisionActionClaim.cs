namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Records one bounded lease claim for a non-approval decision-action wake; a claim is ownership evidence, never release authority.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="ClaimId">The globally unique immutable claim identity.</param>
/// <param name="Wake">The exact published decision-action wake reference.</param>
/// <param name="Reservation">The exact decision-action reservation reference.</param>
/// <param name="ExpectedGeneration">The exact claimed execution generation.</param>
/// <param name="WorkerId">The canonical durable worker identity.</param>
/// <param name="ClaimedAtUtc">The trusted UTC claim time.</param>
/// <param name="LeaseExpiresAtUtc">The exclusive trusted UTC lease-expiry boundary for completion.</param>
/// <param name="Provenance">The immutable trusted coordinator provenance.</param>
/// <param name="ClaimHash">The canonical hash of every behavior-affecting claim field.</param>
public sealed record HumanReviewDecisionActionClaim(int SchemaVersion, string ClaimId, HumanReviewDecisionActionWakeReference Wake, HumanReviewDecisionActionReservationReference Reservation, long ExpectedGeneration, string WorkerId, DateTimeOffset ClaimedAtUtc, DateTimeOffset LeaseExpiresAtUtc, HumanReviewProvenance Provenance, string ClaimHash)
{
    /// <summary>Gets the only supported decision-action claim schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;
}
