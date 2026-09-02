namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies resolution of one exact pinned reconciliation probe.</summary>
public enum GovernedLoopEffectReconciliationProbeRegistryReadStatus
{
    /// <summary>No supported status was established.</summary>
    Unknown = 0,

    /// <summary>The exact registered probe pin was found and resolved.</summary>
    Found = 1,

    /// <summary>No registered probe matched the requested identity.</summary>
    NotFound = 2,

    /// <summary>The registered identity exists but its immutable pin differs.</summary>
    Conflict = 3,

    /// <summary>The exact probe-registry read request was malformed.</summary>
    Invalid = 4,

    /// <summary>The registered probe metadata failed integrity validation.</summary>
    Corrupt = 5,

    /// <summary>The exact registered probe could not be resolved conclusively.</summary>
    Unavailable = 6,
}
