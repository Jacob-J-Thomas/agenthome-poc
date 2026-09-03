namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies request-scoped interface authorization for one exact reconciliation purpose.</summary>
public enum GovernedLoopEffectReconciliationAuthorizationStatus
{
    /// <summary>No supported result was established.</summary>
    Unknown = 0,
    /// <summary>The current authenticated actor and scope authorize this exact purpose.</summary>
    Ready = 1,
    /// <summary>The current actor or scope denies this exact purpose.</summary>
    Denied = 2,
    /// <summary>The exact server-composed request was rejected as malformed.</summary>
    Invalid = 3,
    /// <summary>The current authority evidence failed integrity validation.</summary>
    Corrupt = 4,
    /// <summary>The current interface authority could not be established.</summary>
    Unavailable = 5,
}
