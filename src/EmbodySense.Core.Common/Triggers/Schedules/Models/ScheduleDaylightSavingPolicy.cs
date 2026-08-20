namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Defines explicit gap and fold behavior for local wall-clock recurrence.</summary>
/// <param name="InvalidLocalTime">The policy for a local time inside a forward clock gap.</param>
/// <param name="AmbiguousLocalTime">The policy for a local time inside a backward clock fold.</param>
public sealed record ScheduleDaylightSavingPolicy(
    ScheduleInvalidLocalTimePolicy InvalidLocalTime,
    ScheduleAmbiguousLocalTimePolicy AmbiguousLocalTime);
