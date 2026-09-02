namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies the closed result of one reconciliation orchestration operation.</summary>
public enum GovernedLoopEffectReconciliationOperationStatus
{
    /// <summary>No supported outcome was established.</summary>
    Unknown = 0,

    /// <summary>The immutable case stage and optional effect successor were applied.</summary>
    Applied = 1,

    /// <summary>The exact operation and request hash already produced this result.</summary>
    Replayed = 2,

    /// <summary>An exact case was found without changing it.</summary>
    Found = 3,

    /// <summary>The exact case or input was not found.</summary>
    NotFound = 4,

    /// <summary>Current server-owned authority denied the requested purpose.</summary>
    Denied = 5,

    /// <summary>The exact request or optimistic head conflicted with canonical state.</summary>
    Conflict = 6,

    /// <summary>The request or current state was malformed.</summary>
    Invalid = 7,

    /// <summary>Canonical evidence failed integrity validation.</summary>
    Corrupt = 8,

    /// <summary>The operation could not be established conclusively.</summary>
    Unavailable = 9,

    /// <summary>A finite canonical limit prevents this operation.</summary>
    CapacityExceeded = 10,

    /// <summary>Interrupted atomic intent requires explicit repair.</summary>
    RepairRequired = 11,
}
