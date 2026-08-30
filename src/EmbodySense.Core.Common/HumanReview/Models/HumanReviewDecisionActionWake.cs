namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Publishes one durable, non-authorizing wake for an exact accepted non-approval decision action.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="WakeId">The globally unique immutable wake identity.</param>
/// <param name="Request">The exact immutable review request reference.</param>
/// <param name="Decision">The exact accepted Reject, Cancel, or RequestInformation decision reference.</param>
/// <param name="Reservation">The exact one-time decision-action reservation reference.</param>
/// <param name="BindingHash">The exact immutable run, revision, frontier, activation, and effect binding hash.</param>
/// <param name="ExpectedGeneration">The positive execution generation that cannot be rebound or reused.</param>
/// <param name="PublishedAtUtc">The trusted UTC publication time.</param>
/// <param name="ExpiresAtUtc">The exclusive trusted UTC deadline after which the wake must retire.</param>
/// <param name="Provenance">The immutable trusted coordinator provenance.</param>
/// <param name="WakeHash">The canonical hash of every behavior-affecting wake field.</param>
public sealed record HumanReviewDecisionActionWake(int SchemaVersion, string WakeId, HumanReviewRequestReference Request, HumanReviewDecisionReference Decision, HumanReviewDecisionActionReservationReference Reservation, string BindingHash, long ExpectedGeneration, DateTimeOffset PublishedAtUtc, DateTimeOffset ExpiresAtUtc, HumanReviewProvenance Provenance, string WakeHash)
{
    /// <summary>Gets the only supported decision-action wake schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;
}
