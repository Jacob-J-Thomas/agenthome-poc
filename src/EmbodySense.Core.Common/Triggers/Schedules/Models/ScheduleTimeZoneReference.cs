namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Pins a time-zone identifier to the exact rules snapshot used to resolve local occurrences.</summary>
/// <param name="TimeZoneId">The case-sensitive adapter-resolved time-zone identifier.</param>
/// <param name="RulesFingerprint">The lowercase SHA-256 fingerprint of the exact rule snapshot.</param>
public sealed record ScheduleTimeZoneReference(string TimeZoneId, string RulesFingerprint);
