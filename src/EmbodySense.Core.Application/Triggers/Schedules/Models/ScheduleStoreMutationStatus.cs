namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Defines closed atomic schedule-store mutation outcomes.</summary>
public enum ScheduleStoreMutationStatus
{
    /// <summary>The store returned no recognized outcome.</summary>
    Unknown = 0,
    /// <summary>The exact mutation was durably applied.</summary>
    Applied = 1,
    /// <summary>A create found an existing schedule.</summary>
    AlreadyExists = 2,
    /// <summary>The expected state did not match authoritative state.</summary>
    Conflict = 3,
    /// <summary>The store could not durably decide the mutation.</summary>
    Unavailable = 4,
    /// <summary>Persisted or proposed data failed integrity validation.</summary>
    Corrupt = 5,
    /// <summary>The store refused the mutation under bounded load.</summary>
    Backpressured = 6,
}
