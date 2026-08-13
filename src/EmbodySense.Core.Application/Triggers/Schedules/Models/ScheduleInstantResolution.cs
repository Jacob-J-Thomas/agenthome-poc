namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Returns exact rules evidence and the local wall-clock mapping for one UTC instant.</summary>
public sealed record ScheduleInstantResolution(
    ScheduleInstantResolutionStatus Status,
    string? RulesFingerprint,
    DateTime ScheduledLocal);
