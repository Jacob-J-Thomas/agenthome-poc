namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Defines one closed recurrence rule anchored to an exact local wall-clock occurrence.</summary>
/// <remarks>Once, daily, and weekly recurrences carry no interval. Fixed intervals carry exact elapsed seconds. Time-zone calculation is intentionally outside this dependency-free contract.</remarks>
/// <param name="Kind">The closed recurrence kind.</param>
/// <param name="FirstLocalOccurrence">The first unqualified local wall-clock occurrence.</param>
/// <param name="FixedIntervalSeconds">The exact elapsed interval required only for <see cref="ScheduleRecurrenceKind.FixedInterval"/>.</param>
public sealed record ScheduleRecurrenceRule(
    ScheduleRecurrenceKind Kind,
    DateTime FirstLocalOccurrence,
    long? FixedIntervalSeconds);
