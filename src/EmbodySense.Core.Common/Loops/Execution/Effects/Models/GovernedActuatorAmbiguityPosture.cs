namespace EmbodySense.Core.Common.Loops.Execution.Effects.Models;

/// <summary>Declares the only supported policy when an external outcome cannot be proved.</summary>
public enum GovernedActuatorAmbiguityPosture
{
    /// <summary>No supported posture was selected.</summary>
    Unknown = 0,

    /// <summary>The attempt becomes durably reconciliation-required and is never automatically redispatched.</summary>
    ReconciliationRequired = 1,
}
