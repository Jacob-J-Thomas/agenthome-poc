namespace EmbodySense.Core.Startup.Triggers.Schedules.Models;

/// <summary>Defines closed composition-owned governed payload resolution outcomes.</summary>
public enum ScheduleGovernedPayloadResolutionStatus
{
    /// <summary>No supported posture was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact bounded payload and its digest were resolved.</summary>
    Available = 1,
    /// <summary>The exact opaque identity was not found.</summary>
    NotFound = 2,
    /// <summary>The source could not establish current trustworthy state.</summary>
    Unavailable = 3,
    /// <summary>The source returned contradictory or corrupt evidence.</summary>
    Corrupt = 4,
    /// <summary>The source refused the bounded read under current load.</summary>
    Backpressured = 5,
}
