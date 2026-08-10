namespace EmbodySense.Core.Common.Loops.Execution.Models;

/// <summary>Identifies the synchronization posture of a projection derived from retained source evidence.</summary>
public enum GovernedLoopProjectionStatus
{
    /// <summary>No supported projection posture was supplied.</summary>
    Unknown = 0,
    /// <summary>The projection has not yet committed.</summary>
    Pending,
    /// <summary>The projection committed against its optimistic precondition.</summary>
    Committed,
    /// <summary>The optimistic precondition or observed target state conflicted.</summary>
    Conflict,
    /// <summary>Explicit reconciliation is required before another projection attempt.</summary>
    ReconciliationRequired,
    /// <summary>Explicit reconciliation evidence was retained without rewriting the prior conflict.</summary>
    Reconciled
}
