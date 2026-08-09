namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Identifies an exact lease or dispatch mutation outcome.</summary>
public enum TriggerWorkerMutationStatus
{
    /// <summary>The mutation committed.</summary>
    Committed,

    /// <summary>The exact mutation was already committed.</summary>
    Replayed,

    /// <summary>The entry does not exist.</summary>
    NotFound,

    /// <summary>The entry revision changed.</summary>
    RevisionConflict,

    /// <summary>The worker identity or lease generation is stale.</summary>
    StaleOwner,

    /// <summary>The supplied clock moved behind persisted worker evidence.</summary>
    ClockRollback,

    /// <summary>The requested transition is not valid from the durable state.</summary>
    InvalidState,

    /// <summary>Persistence could not safely commit the transition.</summary>
    Unavailable
}
