namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies reconstruction of exact immutable graph and run inputs for reconciliation.</summary>
public enum GovernedLoopEffectReconciliationInputReadStatus
{
    /// <summary>No supported status was established.</summary>
    Unknown = 0,

    /// <summary>The exact immutable graph and run inputs were found.</summary>
    Found = 1,

    /// <summary>The exact graph or run input no longer exists.</summary>
    NotFound = 2,

    /// <summary>The requested case binding conflicts with canonical graph or run input identity.</summary>
    Conflict = 3,

    /// <summary>The exact input request was malformed.</summary>
    Invalid = 4,

    /// <summary>Canonical graph or run inputs failed integrity validation.</summary>
    Corrupt = 5,

    /// <summary>The exact immutable inputs could not be reconstructed conclusively.</summary>
    Unavailable = 6,
}
