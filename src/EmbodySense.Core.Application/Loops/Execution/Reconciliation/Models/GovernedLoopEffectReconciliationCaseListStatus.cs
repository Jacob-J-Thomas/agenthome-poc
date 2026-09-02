namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies the result of a bounded reconciliation case-list read.</summary>
public enum GovernedLoopEffectReconciliationCaseListStatus
{
    /// <summary>No supported status was established.</summary>
    Unknown = 0,

    /// <summary>The bounded page was read from one coherent canonical snapshot.</summary>
    Ready = 1,

    /// <summary>The request was outside the finite list bounds or otherwise malformed.</summary>
    Invalid = 2,

    /// <summary>The canonical reconciliation case ledger failed integrity validation.</summary>
    Corrupt = 3,

    /// <summary>The canonical reconciliation case ledger could not be read conclusively.</summary>
    Unavailable = 4,
}
