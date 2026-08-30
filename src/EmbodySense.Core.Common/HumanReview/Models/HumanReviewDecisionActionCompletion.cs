using System.Collections.Immutable;

namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Records one conclusive non-approval decision action after its exact fenced worker claim.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="CompletionId">The globally unique completion identity.</param>
/// <param name="Wake">The exact published action wake.</param>
/// <param name="Claim">The exact active action claim.</param>
/// <param name="Reservation">The exact accepted non-approval decision reservation.</param>
/// <param name="ExpectedGeneration">The exact completed execution generation.</param>
/// <param name="Disposition">The exact declared non-approval action that completed.</param>
/// <param name="ResultHash">The value-free hash of the conclusive action result.</param>
/// <param name="FrontierReceiptHash">The value-free hash of the durable frontier receipt, or the unchanged parked-frontier receipt for information requests.</param>
/// <param name="CompletedAtUtc">The trusted UTC completion time.</param>
/// <param name="Evidence">The bounded redacted completion evidence.</param>
/// <param name="Provenance">The trusted coordinator provenance.</param>
/// <param name="CompletionHash">The canonical hash of all behavior-affecting completion fields.</param>
public sealed record HumanReviewDecisionActionCompletion(int SchemaVersion, string CompletionId, HumanReviewDecisionActionWakeReference Wake, HumanReviewDecisionActionClaimReference Claim, HumanReviewDecisionActionReservationReference Reservation, long ExpectedGeneration, HumanReviewDecisionActionDisposition Disposition, string ResultHash, string FrontierReceiptHash, DateTimeOffset CompletedAtUtc, ImmutableArray<HumanReviewRedactedPreview> Evidence, HumanReviewProvenance Provenance, string CompletionHash)
{
    /// <summary>Gets the only supported completion schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;
}
