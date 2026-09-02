namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies the result of a bounded reconciliation probe-registry list read.</summary>
public enum GovernedLoopEffectReconciliationProbeRegistryListStatus
{
    /// <summary>No supported status was established.</summary>
    Unknown = 0,

    /// <summary>The bounded registered-probe page was read from one coherent snapshot.</summary>
    Ready = 1,

    /// <summary>The list request was malformed.</summary>
    Invalid = 2,

    /// <summary>The registered probe metadata failed integrity validation.</summary>
    Corrupt = 3,

    /// <summary>The registered probe metadata could not be read conclusively.</summary>
    Unavailable = 4,
}
