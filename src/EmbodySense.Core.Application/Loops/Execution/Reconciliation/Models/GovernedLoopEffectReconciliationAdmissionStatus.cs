namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Classifies server-owned publication of a durable actuator ambiguity into the reconciliation case store.</summary>
public enum GovernedLoopEffectReconciliationAdmissionStatus
{
    /// <summary>No supported result was established.</summary>
    Unknown = 0,

    /// <summary>A new immutable case was opened.</summary>
    Opened,

    /// <summary>The exact prior case-opening operation was replayed.</summary>
    Replayed,

    /// <summary>The run does not carry a qualifying reconciliation-required ambiguity.</summary>
    NotApplicable,

    /// <summary>The server-owned admission authority denied publication of the case.</summary>
    Denied,

    /// <summary>The exact retained evidence changed or conflicts with existing state.</summary>
    Conflict,

    /// <summary>The retained input is invalid.</summary>
    Invalid,

    /// <summary>Canonical evidence is malformed or corrupt.</summary>
    Corrupt,

    /// <summary>The canonical store or a required dependency is unavailable.</summary>
    Unavailable,

    /// <summary>The finite reconciliation capacity is exhausted.</summary>
    CapacityExceeded,

    /// <summary>Incomplete durable evidence requires repair before admission can continue.</summary>
    RepairRequired,
}
