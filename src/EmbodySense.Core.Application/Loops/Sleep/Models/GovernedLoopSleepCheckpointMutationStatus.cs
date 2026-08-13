namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Classifies one checkpoint store mutation.</summary>
public enum GovernedLoopSleepCheckpointMutationStatus
{
    /// <summary>The exact checkpoint became durable before ownership release.</summary>
    Committed = 1,
    /// <summary>The exact deterministic checkpoint was already durable.</summary>
    Replayed = 2,
    /// <summary>An optimistic or immutable-identity conflict prevented the mutation.</summary>
    Conflict = 3,
    /// <summary>The store was conclusively unavailable before a mutation.</summary>
    Unavailable = 4,
    /// <summary>The store may have committed and must be reconciled by identity.</summary>
    Ambiguous = 5
}
