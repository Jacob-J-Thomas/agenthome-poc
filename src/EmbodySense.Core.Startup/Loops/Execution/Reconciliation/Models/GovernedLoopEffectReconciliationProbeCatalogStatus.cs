namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies a bounded registered reconciliation-probe catalog read.</summary>
public enum GovernedLoopEffectReconciliationProbeCatalogStatus
{
    /// <summary>No supported result was established.</summary>
    Unknown = 0,
    /// <summary>The exact bounded registry page was read.</summary>
    Ready = 1,
    /// <summary>The request was malformed.</summary>
    Invalid = 2,
    /// <summary>The registered catalog failed integrity validation.</summary>
    Corrupt = 3,
    /// <summary>The registered catalog could not be read conclusively.</summary>
    Unavailable = 4,
}
