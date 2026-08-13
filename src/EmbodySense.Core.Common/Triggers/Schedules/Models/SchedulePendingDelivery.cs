namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Represents a durably claimed occurrence whose exact delivery outcome is not yet finalized.</summary>
/// <param name="SchemaVersion">The exact schema version, which must be 1.</param>
/// <param name="Phase">The closed durable pending-delivery phase.</param>
/// <param name="Occurrence">The exact pending local and UTC occurrence.</param>
/// <param name="Identity">The deterministic occurrence, delivery, and deduplication identities.</param>
/// <param name="ClaimId">The optimistic store claim identity.</param>
/// <param name="ClaimedAtUtc">When the pending claim became durable.</param>
/// <param name="CurrentEvidenceHash">The exact fresh authority and current-target evidence hash used by the latest prepared or observed admission attempt.</param>
/// <param name="RecurrenceProofHash">The exact deterministic recurrence and successor-calculation proof hash, once prepared.</param>
/// <param name="OverlapEvidenceHash">The exact current governed-run overlap evidence hash, once prepared.</param>
/// <param name="FinalizationPlan">The immutable successor plan persisted before queue admission.</param>
/// <param name="Prepared">The optional exact envelope persisted before queue admission.</param>
/// <param name="Result">The optional durable queue result observed before finalization.</param>
public sealed record SchedulePendingDelivery(
    int SchemaVersion,
    SchedulePendingDeliveryPhase Phase,
    ScheduleOccurrence Occurrence,
    ScheduleOccurrenceIdentity Identity,
    ScheduleClaimId ClaimId,
    DateTimeOffset ClaimedAtUtc,
    string? CurrentEvidenceHash,
    string? RecurrenceProofHash,
    string? OverlapEvidenceHash,
    ScheduleFinalizationPlan? FinalizationPlan,
    SchedulePreparedDelivery? Prepared,
    ScheduleDeliveryResultEvidence? Result)
{
    /// <summary>Gets the only supported pending-delivery schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;
}
