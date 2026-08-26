namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

/// <summary>Classifies one explicit local coordinator start request.</summary>
public enum GovernedLoopLocalCoordinatorStartStatus
{
    /// <summary>The exact owner acquired evidence and entered running posture.</summary>
    Started = 1,

    /// <summary>This coordinator object was already running.</summary>
    AlreadyRunning = 2,

    /// <summary>Another live owner retains the exclusive coordinator lease.</summary>
    OwnedByLivePeer = 3,

    /// <summary>An optimistic acquisition or lifecycle race requires a fresh start attempt.</summary>
    Conflict = 4,

    /// <summary>Retained or returned coordinator evidence was malformed or corrupt.</summary>
    Corrupt = 5,

    /// <summary>A required durable coordinator dependency was unavailable.</summary>
    Unavailable = 6,

    /// <summary>The previous owned session durably terminated fail closed and requires explicit repair before restart.</summary>
    Failed = 7
}
