namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies one closed reconciliation operation outcome.</summary>
public enum GovernedLoopEffectReconciliationOperationStatus
{
    /// <summary>No supported result was established.</summary>
    Unknown = 0,
    /// <summary>The immutable case stage was applied.</summary>
    Applied = 1,
    /// <summary>The exact operation replayed its canonical result.</summary>
    Replayed = 2,
    /// <summary>The exact case was found without mutation.</summary>
    Found = 3,
    /// <summary>The exact case or current input was not found.</summary>
    NotFound = 4,
    /// <summary>Current server-owned authority denied the purpose.</summary>
    Denied = 5,
    /// <summary>The request conflicted with canonical state.</summary>
    Conflict = 6,
    /// <summary>The request or current state was malformed.</summary>
    Invalid = 7,
    /// <summary>Canonical evidence failed integrity validation.</summary>
    Corrupt = 8,
    /// <summary>The operation could not be established conclusively.</summary>
    Unavailable = 9,
    /// <summary>A finite canonical limit prevented the operation.</summary>
    CapacityExceeded = 10,
    /// <summary>Interrupted durable intent requires explicit repair.</summary>
    RepairRequired = 11,
}
