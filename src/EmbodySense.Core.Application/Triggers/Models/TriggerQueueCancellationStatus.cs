namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Defines outcomes for an optimistic queue cancellation.</summary>
public enum TriggerQueueCancellationStatus
{
    /// <summary>The queued entry was durably cancelled.</summary>
    Cancelled,

    /// <summary>The entry was already terminal.</summary>
    AlreadyTerminal,

    /// <summary>No matching delivery exists.</summary>
    NotFound,

    /// <summary>The expected entry revision was stale.</summary>
    RevisionConflict,

    /// <summary>The authenticated cleanup-tombstone quota cannot reserve the cancellation mutation.</summary>
    PersistenceBackpressured,

    /// <summary>The ledger could not be inspected or changed safely.</summary>
    Unavailable
}
