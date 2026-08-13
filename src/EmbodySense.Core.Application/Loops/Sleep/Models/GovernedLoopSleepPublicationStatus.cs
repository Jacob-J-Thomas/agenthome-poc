namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Classifies one bounded sleep-checkpoint publication result.</summary>
public enum GovernedLoopSleepPublicationStatus
{
    /// <summary>The checkpoint was durably published before ownership release.</summary>
    Published = 1,
    /// <summary>The same deterministic checkpoint was already durable.</summary>
    Replayed = 2,
    /// <summary>The request or an adapter result was malformed.</summary>
    Invalid = 3,
    /// <summary>The exact execution could not be found.</summary>
    NotFound = 4,
    /// <summary>The requested generation, frontier, activation, cycle, visit, or attempt is no longer current.</summary>
    Stale = 5,
    /// <summary>An optimistic conflict prevented publication.</summary>
    Conflict = 6,
    /// <summary>The exact run was cancelled.</summary>
    Cancelled = 7,
    /// <summary>The exact run expired or already terminated.</summary>
    Expired = 8,
    /// <summary>The exact run is paused.</summary>
    Paused = 9,
    /// <summary>Unattended continuation is not authorized or explicit review is required.</summary>
    ReviewBlocked = 10,
    /// <summary>An open or ambiguous effect attempt forbids release.</summary>
    AmbiguousAttempt = 11,
    /// <summary>A required authoritative dependency was conclusively unavailable.</summary>
    Unavailable = 12,
    /// <summary>The durable publication outcome could not be determined safely.</summary>
    Ambiguous = 13
}
