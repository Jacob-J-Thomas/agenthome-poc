namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Classifies one authoritative current-posture read.</summary>
public enum GovernedLoopSleepCurrentPostureReadStatus
{
    /// <summary>The exact current posture was found.</summary>
    Found = 1,
    /// <summary>The exact run or generation was not found.</summary>
    NotFound = 2,
    /// <summary>Concurrent authoritative evidence prevented a consistent read.</summary>
    Conflict = 3,
    /// <summary>The authoritative posture source was unavailable.</summary>
    Unavailable = 4
}
