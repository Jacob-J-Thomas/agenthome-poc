namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Defines closed local-time resolution outcomes.</summary>
public enum ScheduleTimeZoneResolutionStatus
{
    /// <summary>No recognized resolution was returned.</summary>
    Unknown = 0,
    /// <summary>The local time has one UTC mapping.</summary>
    Unique = 1,
    /// <summary>The local time is in a gap.</summary>
    InvalidLocalTime = 2,
    /// <summary>The local time has an ordered earlier and later UTC mapping.</summary>
    AmbiguousLocalTime = 3,
    /// <summary>Current time-zone evidence was unavailable.</summary>
    Unavailable = 4,
    /// <summary>Time-zone evidence failed integrity validation.</summary>
    Corrupt = 5,
    /// <summary>The rules source refused the read under bounded load.</summary>
    Backpressured = 6,
}
