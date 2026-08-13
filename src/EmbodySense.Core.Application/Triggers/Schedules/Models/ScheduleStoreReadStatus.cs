namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Defines closed schedule-store read outcomes.</summary>
public enum ScheduleStoreReadStatus
{
    /// <summary>The store returned no recognized outcome.</summary>
    Unknown = 0,
    /// <summary>The exact definition and state were found.</summary>
    Found = 1,
    /// <summary>The schedule does not exist.</summary>
    NotFound = 2,
    /// <summary>The store could not be reached.</summary>
    Unavailable = 3,
    /// <summary>Persisted schedule data failed validation.</summary>
    Corrupt = 4,
    /// <summary>The store refused the read under bounded load.</summary>
    Backpressured = 5,
}
