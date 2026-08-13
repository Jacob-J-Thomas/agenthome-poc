namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Captures one exact, time-zone-resolved schedule occurrence.</summary>
/// <param name="SchemaVersion">The exact schema version, which must be 1.</param>
/// <param name="Ordinal">The positive occurrence ordinal within one definition lineage.</param>
/// <param name="ScheduledLocal">The exact unqualified local wall-clock occurrence.</param>
/// <param name="ScheduledAtUtc">The exact selected UTC occurrence.</param>
/// <param name="TimeZone">The exact time-zone identifier and rules fingerprint used for selection.</param>
public sealed record ScheduleOccurrence(
    int SchemaVersion,
    long Ordinal,
    DateTime ScheduledLocal,
    DateTimeOffset ScheduledAtUtc,
    ScheduleTimeZoneReference TimeZone)
{
    /// <summary>Gets the only supported occurrence schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;
}
