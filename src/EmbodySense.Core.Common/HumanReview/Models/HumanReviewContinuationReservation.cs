namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Defines one server-owned reservation of the exact continuation bound to one accepted approval; it grants no release authority.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="ReservationId">The globally unique reservation identity.</param>
/// <param name="Request">The exact immutable request reference.</param>
/// <param name="Decision">The exact accepted approval decision reference.</param>
/// <param name="ReservedAtUtc">The immutable trusted UTC reservation time and deterministic wake-publication timestamp. A publisher must replay this exact value after an uncertain response and must not obtain a replacement clock value.</param>
/// <param name="Provenance">The trusted server or coordinator provenance.</param>
/// <param name="ReservationHash">The canonical hash of every behavior-affecting reservation field.</param>
public sealed record HumanReviewContinuationReservation(int SchemaVersion, string ReservationId, HumanReviewRequestReference Request, HumanReviewDecisionReference Decision, DateTimeOffset ReservedAtUtc, HumanReviewProvenance Provenance, string ReservationHash)
{
    /// <summary>Gets the only supported reservation schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;
}
