namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies a bounded reconciliation attention-page result.</summary>
public enum GovernedLoopEffectReconciliationPageStatus
{
    /// <summary>No supported result was established.</summary>
    Unknown = 0,
    /// <summary>The page was read from one coherent canonical snapshot.</summary>
    Ready = 1,
    /// <summary>The request was malformed or outside finite bounds.</summary>
    Invalid = 2,
    /// <summary>Canonical case evidence failed integrity validation.</summary>
    Corrupt = 3,
    /// <summary>The canonical case ledger could not be read conclusively.</summary>
    Unavailable = 4,
}
