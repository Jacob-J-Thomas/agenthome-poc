namespace EmbodySense.Core.Common.Loops.Failures.Models;

/// <summary>Retains authenticated human lifecycle posture relevant to a failure.</summary>
public enum GovernedLoopFailureHumanPosture
{
    /// <summary>No trustworthy human posture is available.</summary>
    Unknown = 0,
    /// <summary>No human lifecycle action controls this observation.</summary>
    None,
    /// <summary>An authenticated reviewer rejected the operation.</summary>
    ReviewRejected,
    /// <summary>An authenticated user paused the run.</summary>
    Paused,
    /// <summary>An authenticated user cancelled the run.</summary>
    Cancelled,
}
