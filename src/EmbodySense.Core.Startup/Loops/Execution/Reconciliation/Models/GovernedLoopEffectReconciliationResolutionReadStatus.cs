namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies an immutable reconciliation resolution read.</summary>
public enum GovernedLoopEffectReconciliationResolutionReadStatus
{
    /// <summary>No supported result was established.</summary>
    Unknown = 0,
    /// <summary>The exact immutable resolution was found.</summary>
    Found = 1,
    /// <summary>No immutable resolution exists for the exact case.</summary>
    NotFound = 2,
    /// <summary>The exact request was malformed.</summary>
    Invalid = 3,
    /// <summary>The immutable resolution failed integrity validation.</summary>
    Corrupt = 4,
    /// <summary>The resolution could not be read conclusively.</summary>
    Unavailable = 5,
}
