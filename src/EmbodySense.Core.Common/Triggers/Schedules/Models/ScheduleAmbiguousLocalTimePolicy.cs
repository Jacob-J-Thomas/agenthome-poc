namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Defines policy for a local wall-clock time with two UTC mappings.</summary>
public enum ScheduleAmbiguousLocalTimePolicy
{
    /// <summary>The policy is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>Select the earlier UTC occurrence explicitly.</summary>
    EarlierUtc = 1,
    /// <summary>Select the later UTC occurrence explicitly.</summary>
    LaterUtc = 2,
}
