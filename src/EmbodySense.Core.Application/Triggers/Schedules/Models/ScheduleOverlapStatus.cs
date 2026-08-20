namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Defines closed exact overlap outcomes.</summary>
public enum ScheduleOverlapStatus
{
    /// <summary>No recognized status was returned.</summary>
    Unknown = 0,
    /// <summary>No exact active governed run overlaps the occurrence.</summary>
    Clear = 1,
    /// <summary>An exact active governed run overlaps the occurrence.</summary>
    Active = 2,
    /// <summary>Current overlap evidence was unavailable.</summary>
    Unavailable = 3,
    /// <summary>Overlap evidence failed integrity validation.</summary>
    Corrupt = 4,
    /// <summary>The evidence source refused the read under bounded load.</summary>
    Backpressured = 5,
}
