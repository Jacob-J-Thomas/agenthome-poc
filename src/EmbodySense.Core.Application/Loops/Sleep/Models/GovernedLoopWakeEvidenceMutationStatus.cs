namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Classifies one wake-evidence store mutation.</summary>
public enum GovernedLoopWakeEvidenceMutationStatus
{
    /// <summary>The proposed evidence became durable.</summary>
    Committed = 1,
    /// <summary>The same exact evidence was already durable.</summary>
    Replayed = 2,
    /// <summary>A different deterministic wake already claimed the checkpoint.</summary>
    CheckpointClaimed = 3,
    /// <summary>An optimistic or immutable-identity conflict prevented the mutation.</summary>
    Conflict = 4,
    /// <summary>The store was conclusively unavailable before a mutation.</summary>
    Unavailable = 5,
    /// <summary>The store may have committed and must be reconciled by identity.</summary>
    Ambiguous = 6
}
