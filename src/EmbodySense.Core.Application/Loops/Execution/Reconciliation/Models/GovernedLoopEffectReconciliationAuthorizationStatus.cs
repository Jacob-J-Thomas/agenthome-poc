namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies current authorization for one exact reconciliation purpose, case, and binding.</summary>
public enum GovernedLoopEffectReconciliationAuthorizationStatus
{
    /// <summary>No supported status was established.</summary>
    Unknown = 0,

    /// <summary>Current server-owned authority permits the exact reconciliation purpose.</summary>
    Ready = 1,

    /// <summary>Current server-owned authority denies the exact reconciliation purpose.</summary>
    Denied = 2,

    /// <summary>The exact authorization request was malformed.</summary>
    Invalid = 3,

    /// <summary>Authorization evidence failed integrity validation.</summary>
    Corrupt = 4,

    /// <summary>Current authority could not be established conclusively.</summary>
    Unavailable = 5,
}
