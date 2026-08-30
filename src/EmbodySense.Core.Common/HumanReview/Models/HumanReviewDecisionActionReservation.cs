namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Defines one durable reservation for an accepted Reject, Cancel, or RequestInformation action; it grants no release authority.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="ReservationId">The globally unique reservation identity.</param>
/// <param name="Request">The exact immutable review request reference.</param>
/// <param name="Decision">The exact accepted non-approval decision reference.</param>
/// <param name="ReservedAtUtc">The trusted reservation time.</param>
/// <param name="Provenance">The trusted server or coordinator provenance.</param>
/// <param name="ReservationHash">The canonical hash of every behavior-affecting reservation field.</param>
public sealed record HumanReviewDecisionActionReservation(int SchemaVersion, string ReservationId, HumanReviewRequestReference Request, HumanReviewDecisionReference Decision, DateTimeOffset ReservedAtUtc, HumanReviewProvenance Provenance, string ReservationHash)
{
    /// <summary>Gets the only supported decision-action reservation schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;
}
