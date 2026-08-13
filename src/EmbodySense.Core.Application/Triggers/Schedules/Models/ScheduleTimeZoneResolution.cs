namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Returns exact rules evidence and ordered UTC mappings for one local occurrence.</summary>
public sealed record ScheduleTimeZoneResolution(
    ScheduleTimeZoneResolutionStatus Status,
    string? RulesFingerprint,
    DateTime ResolvedLocal,
    DateTimeOffset? EarlierUtc,
    DateTimeOffset? LaterUtc);
