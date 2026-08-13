namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Projects one exact, durably accepted schedule occurrence for authenticated dispatch.</summary>
/// <remarks>
/// This value is derived from the authoritative schedule store. It does not grant authority: consumers must still
/// validate every definition, occurrence, identity, result, and trigger-envelope binding before admitting a run.
/// </remarks>
public sealed record ScheduleDeliveryProvenanceEvidence(
    int SchemaVersion,
    ScheduleDefinition Definition,
    string DefinitionHash,
    ScheduleOccurrence Occurrence,
    ScheduleOccurrenceIdentity Identity,
    ScheduleDeliveryResultEvidence Result)
{
    /// <summary>Gets the only supported provenance-evidence schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;
}
