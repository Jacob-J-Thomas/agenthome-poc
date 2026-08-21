namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Classifies one bounded local coordinator failure without embedding sensitive values.</summary>
public enum GovernedLoopCoordinatorFailureKind
{
    /// <summary>The exact ownership claim was lost or replaced.</summary>
    OwnershipLost = 1,

    /// <summary>The exact heartbeat lease expired before safe renewal.</summary>
    HeartbeatExpired = 2,

    /// <summary>A required durable store was conclusively unavailable.</summary>
    StoreUnavailable = 3,

    /// <summary>Authenticated retained state was malformed or corrupt.</summary>
    CorruptState = 4,

    /// <summary>A bounded fairness or capacity limit prevented safe admission.</summary>
    Backpressured = 5,

    /// <summary>Shutdown interrupted work outside an already-safe persistence boundary.</summary>
    ShutdownInterrupted = 6,

    /// <summary>An unexpected local coordinator failure was retained by reference.</summary>
    Unexpected = 7
}
