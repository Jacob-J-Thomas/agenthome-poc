namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies an exact immutable reconciliation case read.</summary>
public enum GovernedLoopEffectReconciliationReadStatus
{
    /// <summary>No supported result was established.</summary>
    Unknown = 0,
    /// <summary>The exact immutable case was found.</summary>
    Found = 1,
    /// <summary>The exact immutable case was not found.</summary>
    NotFound = 2,
    /// <summary>The request was malformed.</summary>
    Invalid = 3,
    /// <summary>Canonical case evidence failed integrity validation.</summary>
    Corrupt = 4,
    /// <summary>The canonical case could not be read conclusively.</summary>
    Unavailable = 5,
}
