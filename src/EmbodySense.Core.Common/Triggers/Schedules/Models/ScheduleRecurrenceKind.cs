namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Defines the closed schema-1 recurrence catalog.</summary>
public enum ScheduleRecurrenceKind
{
    /// <summary>The recurrence kind is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The schedule has exactly one occurrence.</summary>
    Once = 1,
    /// <summary>Occurrences advance by one exact bounded elapsed interval.</summary>
    FixedInterval = 2,
    /// <summary>Occurrences repeat at the anchored local wall-clock time each day.</summary>
    Daily = 3,
    /// <summary>Occurrences repeat at the anchored local weekday and wall-clock time.</summary>
    Weekly = 4,
}
