namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Classifies one checkpoint or wake-evidence read.</summary>
public enum GovernedLoopSleepStoreReadStatus
{
    /// <summary>The exact artifact was found.</summary>
    Found = 1,
    /// <summary>No artifact exists for the exact identity.</summary>
    NotFound = 2,
    /// <summary>Conflicting indexes or authenticated state prevented a conclusive read.</summary>
    Conflict = 3,
    /// <summary>The store was conclusively unavailable.</summary>
    Unavailable = 4
}
