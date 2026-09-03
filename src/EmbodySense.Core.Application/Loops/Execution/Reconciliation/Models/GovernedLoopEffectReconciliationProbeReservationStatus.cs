namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies the durable reservation disposition for one probe operation.</summary>
public enum GovernedLoopEffectReconciliationProbeReservationStatus
{
    /// <summary>No supported disposition was established.</summary>
    Unknown = 0,
    /// <summary>This call durably reserved the one allowed callback.</summary>
    Reserved = 1,
    /// <summary>The operation already has the same durable reservation.</summary>
    Replayed = 2,
    /// <summary>The operation identity names different immutable intent.</summary>
    Conflict = 3,
    /// <summary>The request is malformed.</summary>
    Invalid = 4,
    /// <summary>Canonical evidence is corrupt.</summary>
    Corrupt = 5,
    /// <summary>The outcome cannot be established safely.</summary>
    Unavailable = 6,
    /// <summary>A finite probe limit prevents reservation.</summary>
    CapacityExceeded = 7,
    /// <summary>Interrupted durable intent requires explicit repair.</summary>
    RepairRequired = 8
}
