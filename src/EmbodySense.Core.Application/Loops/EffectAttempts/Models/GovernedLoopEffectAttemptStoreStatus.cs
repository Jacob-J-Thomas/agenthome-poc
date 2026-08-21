namespace EmbodySense.Core.Application.Loops.EffectAttempts.Models;

/// <summary>Identifies one closed durable effect-attempt store outcome.</summary>
public enum GovernedLoopEffectAttemptStoreStatus
{
    /// <summary>A new prepared intent or direct successor was committed durably.</summary>
    Created = 1,

    /// <summary>The exact existing intent or successor was replayed.</summary>
    Replayed = 2,

    /// <summary>The operation identity, generation, intent, expected hash, successor, or owner lease conflicted.</summary>
    Conflict = 3,

    /// <summary>The exact unfinished attempt is currently owned by another executor.</summary>
    OperationInProgress = 4,

    /// <summary>Finite retained-artifact or byte capacity prevented the mutation.</summary>
    Backpressured = 5,

    /// <summary>Retained attempt evidence was malformed, noncanonical, or structurally unsafe.</summary>
    Corrupt = 6,

    /// <summary>The durable attempt source was unavailable.</summary>
    Unavailable = 7,

    /// <summary>No durable attempt exists for the exact stable operation generation.</summary>
    NotFound = 8,
}
