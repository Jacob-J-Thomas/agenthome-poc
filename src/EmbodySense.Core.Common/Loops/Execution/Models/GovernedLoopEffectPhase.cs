namespace EmbodySense.Core.Common.Loops.Execution.Models;

/// <summary>Identifies the durable phase of one canonical effect attempt without prescribing recovery policy.</summary>
public enum GovernedLoopEffectPhase
{
    /// <summary>No supported phase was supplied.</summary>
    Unknown = 0,
    /// <summary>The canonical intent and idempotency identity have been retained.</summary>
    IntentPrepared,
    /// <summary>Evidence proves irreversible dispatch did not start.</summary>
    DispatchNotStarted,
    /// <summary>The irreversible dispatch boundary was reached.</summary>
    DispatchBoundaryReached,
    /// <summary>A conclusive or conflicting external outcome was observed and retained.</summary>
    OutcomeObserved,
    /// <summary>The result and required local evidence or projection were committed.</summary>
    Committed,
    /// <summary>Ambiguity or conflict requires explicit reconciliation or review.</summary>
    ReconciliationRequired,
    /// <summary>Explicit reconciliation or human disposition evidence was retained.</summary>
    Reconciled
}
