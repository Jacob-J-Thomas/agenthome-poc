namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies the result of an exact reconciliation case read.</summary>
public enum GovernedLoopEffectReconciliationCaseReadStatus
{
    /// <summary>No supported status was established.</summary>
    Unknown = 0,

    /// <summary>The exact immutable case was found.</summary>
    Found = 1,

    /// <summary>No case matched the exact reference.</summary>
    NotFound = 2,

    /// <summary>The exact reference was malformed.</summary>
    Invalid = 3,

    /// <summary>The referenced canonical case failed integrity validation.</summary>
    Corrupt = 4,

    /// <summary>The exact read could not be completed conclusively.</summary>
    Unavailable = 5,
}
