namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies the result of an immutable reconciliation resolution read.</summary>
public enum GovernedLoopEffectReconciliationResolutionReadStatus
{
    /// <summary>No supported status was established.</summary>
    Unknown = 0,

    /// <summary>The exact immutable resolution was found.</summary>
    Found = 1,

    /// <summary>No immutable resolution exists for the exact case reference.</summary>
    NotFound = 2,

    /// <summary>The exact resolution read request was malformed.</summary>
    Invalid = 3,

    /// <summary>The immutable resolution failed integrity validation.</summary>
    Corrupt = 4,

    /// <summary>The exact resolution read could not be completed conclusively.</summary>
    Unavailable = 5,
}
