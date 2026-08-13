namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Defines policy for a local wall-clock time that does not exist.</summary>
public enum ScheduleInvalidLocalTimePolicy
{
    /// <summary>The policy is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>Skip the invalid local occurrence with durable evidence.</summary>
    Skip = 1,
    /// <summary>Shift to the first valid local instant after the gap.</summary>
    ShiftForward = 2,
}
