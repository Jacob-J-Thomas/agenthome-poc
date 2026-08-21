namespace EmbodySense.Core.Common.Loops.Execution.Effects.Models;

/// <summary>Declares the idempotency guarantee an actuator operation can support.</summary>
public enum GovernedActuatorIdempotencyPosture
{
    /// <summary>No supported posture was selected.</summary>
    Unknown = 0,

    /// <summary>The actuator honors the protocol's stable operation identity for exact replay.</summary>
    StableOperationIdentity = 1,

    /// <summary>The actuator cannot prove external idempotency and requires reconciliation after ambiguity.</summary>
    ReconciliationOnly = 2,
}
