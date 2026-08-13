namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

/// <summary>Classifies one explicit local coordinator stop request.</summary>
public enum GovernedLoopLocalCoordinatorStopStatus
{
    /// <summary>New acquisition stopped and current work drained to a safe boundary.</summary>
    Stopped = 1,

    /// <summary>No local coordinator session was running.</summary>
    AlreadyStopped = 2,

    /// <summary>The coordinator failed closed before a normal stopped transition.</summary>
    Failed = 3,

    /// <summary>The exact durable ownership was lost while stopping.</summary>
    OwnershipLost = 4,

    /// <summary>Durable terminal evidence could not be safely persisted or read.</summary>
    Unavailable = 5
}
