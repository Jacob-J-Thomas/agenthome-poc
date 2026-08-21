namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Identifies the closed posture returned by an authenticated actuator recovery probe.</summary>
public enum GovernedActuatorProbePosture
{
    /// <summary>Trusted recovery evidence was unavailable or malformed.</summary>
    Unavailable = 0,

    /// <summary>Exact retained evidence proves the external effect did not start.</summary>
    ProvedNotStarted = 1,

    /// <summary>Exact retained evidence proves one conclusive external outcome.</summary>
    OutcomeObserved = 2,

    /// <summary>Trusted evidence exists but cannot prove one conclusive outcome.</summary>
    Indeterminate = 3,
}
