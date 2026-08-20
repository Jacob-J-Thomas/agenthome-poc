namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Defines closed UTC-instant resolution outcomes.</summary>
public enum ScheduleInstantResolutionStatus
{
    /// <summary>No recognized resolution was returned.</summary>
    Unknown = 0,
    /// <summary>The instant has one exact local wall-clock mapping.</summary>
    Resolved = 1,
    /// <summary>Current time-zone evidence was unavailable.</summary>
    Unavailable = 2,
    /// <summary>Time-zone evidence failed integrity validation.</summary>
    Corrupt = 3,
    /// <summary>The rules source refused the read under bounded load.</summary>
    Backpressured = 4,
}
