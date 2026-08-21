namespace EmbodySense.Core.Application.Loops.Wait.Models;

/// <summary>Classifies one executable Wait park attempt.</summary>
public enum GovernedLoopWaitParkResultStatus
{
    /// <summary>The request or retained runtime was invalid.</summary>
    Invalid = 0,
    /// <summary>The Wait frontier, checkpoint, and park evidence committed.</summary>
    Parked,
    /// <summary>The exact prior park was replayed.</summary>
    Replayed,
    /// <summary>The runtime or checkpoint does not exist.</summary>
    NotFound,
    /// <summary>Current state conflicts with the requested exact frontier or evidence.</summary>
    Conflict,
    /// <summary>The required store is unavailable.</summary>
    Unavailable,
    /// <summary>Durable evidence may have committed and must be reconciled.</summary>
    Ambiguous,
    /// <summary>The run has been cancelled.</summary>
    Cancelled,
    /// <summary>The run or admitted execution deadline has expired.</summary>
    Expired,
    /// <summary>Current policy requires explicit review.</summary>
    ReviewBlocked,
}
