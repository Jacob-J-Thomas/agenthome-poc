namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Freezes one bounded catch-up episode so repeated evaluators cannot renew its delivery budget.</summary>
/// <param name="SchemaVersion">The exact schema version, which must be 1.</param>
/// <param name="LatestDueOrdinal">The immutable latest-due ordinal observed when the episode began.</param>
/// <param name="RemainingAdmittedOccurrences">The remaining one-shot catch-up deliveries admitted by the original policy decision.</param>
public sealed record ScheduleCatchUpEpisode(
    int SchemaVersion,
    long LatestDueOrdinal,
    int RemainingAdmittedOccurrences)
{
    /// <summary>Gets the only supported catch-up schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;
}
