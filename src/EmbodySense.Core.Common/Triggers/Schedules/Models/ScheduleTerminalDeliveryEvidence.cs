namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Retains one bounded terminal queue-admission result after its pending delivery is finalized.</summary>
/// <param name="SchemaVersion">The exact schema version, which must be 1.</param>
/// <param name="Occurrence">The exact occurrence whose pending state was finalized.</param>
/// <param name="Identity">The deterministic occurrence and queue identities.</param>
/// <param name="CurrentEvidenceHash">The exact fresh authority and current-target evidence hash used by the conclusive admission attempt.</param>
/// <param name="RecurrenceProofHash">The exact recurrence and successor-calculation proof hash applied at finalization.</param>
/// <param name="OverlapEvidenceHash">The exact governed-run overlap evidence hash used to select the policy path.</param>
/// <param name="Result">The conclusive queue-admission result.</param>
/// <param name="FinalizedAtUtc">When pending state was atomically cleared and its plan applied.</param>
public sealed record ScheduleTerminalDeliveryEvidence(
    int SchemaVersion,
    ScheduleOccurrence Occurrence,
    ScheduleOccurrenceIdentity Identity,
    string CurrentEvidenceHash,
    string RecurrenceProofHash,
    string OverlapEvidenceHash,
    ScheduleDeliveryResultEvidence Result,
    DateTimeOffset FinalizedAtUtc)
{
    /// <summary>Gets the only supported terminal-evidence schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;
}
