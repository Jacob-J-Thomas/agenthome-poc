namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies the result of one read-only reconciliation probe invocation.</summary>
public enum GovernedLoopEffectReconciliationProbeInvocationStatus
{
    /// <summary>No supported status was established.</summary>
    Unknown = 0,

    /// <summary>The read-only probe produced a bounded observation.</summary>
    Ready = 1,

    /// <summary>The exact external subject was not found.</summary>
    NotFound = 2,

    /// <summary>The exact probe invocation request was malformed.</summary>
    Invalid = 3,

    /// <summary>The probe or returned observation failed integrity validation.</summary>
    Corrupt = 4,

    /// <summary>The read-only observation could not be completed conclusively.</summary>
    Unavailable = 5,
}
