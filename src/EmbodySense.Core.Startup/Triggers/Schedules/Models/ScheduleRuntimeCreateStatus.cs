namespace EmbodySense.Core.Startup.Triggers.Schedules.Models;

/// <summary>Defines closed one-shot schedule creation outcomes.</summary>
public enum ScheduleRuntimeCreateStatus
{
    /// <summary>No supported outcome was produced.</summary>
    Unknown = 0,
    /// <summary>The exact definition and composition-derived revision-1 state were durably created.</summary>
    Created = 1,
    /// <summary>The exact immutable definition already exists with trustworthy state.</summary>
    AlreadyExists = 2,
    /// <summary>A different immutable definition already owns the schedule identity.</summary>
    Conflict = 3,
    /// <summary>The bounded initial recurrence scan could not reach a valid occurrence.</summary>
    BoundExceeded = 4,
    /// <summary>A required store, clock, or time-zone source was unavailable.</summary>
    Unavailable = 5,
    /// <summary>Definition, time-zone, or persisted evidence was malformed or contradictory.</summary>
    Corrupt = 6,
    /// <summary>A durable dependency refused the bounded operation under current load.</summary>
    Backpressured = 7,
}
