namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Classifies the observed outcome of one exact idempotent continuation operation.</summary>
public enum GovernedLoopWakeContinuationStatus
{
    /// <summary>Exact durable evidence proves the continuation committed.</summary>
    Committed = 1,
    /// <summary>Exact durable evidence proves the continuation did not commit.</summary>
    NotCommitted = 2,
    /// <summary>Available evidence cannot determine whether the continuation committed.</summary>
    Ambiguous = 3,
    /// <summary>Different immutable evidence is bound to the operation.</summary>
    Conflict = 4,
    /// <summary>The continuation evidence source was unavailable and the outcome is unknown.</summary>
    Unavailable = 5
}
