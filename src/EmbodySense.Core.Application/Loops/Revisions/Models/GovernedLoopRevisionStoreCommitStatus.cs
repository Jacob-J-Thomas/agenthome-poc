namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Identifies the exact atomic persistence disposition of one lifecycle mutation.</summary>
public enum GovernedLoopRevisionStoreCommitStatus
{
    /// <summary>An undefined result that a conforming store never returns.</summary>
    Unknown = 0,
    /// <summary>The mutation and terminal operation evidence were committed atomically.</summary>
    Committed = 1,
    /// <summary>The exact operation and request hash had already reached the returned terminal outcome.</summary>
    Replayed = 2,
    /// <summary>The global store generation changed before commit.</summary>
    StoreConflict = 3,
    /// <summary>The operation identifier was already bound to a different graph or request hash.</summary>
    OperationConflict = 4,
    /// <summary>No durable intent was published because the store was unavailable.</summary>
    Unavailable = 5,
    /// <summary>Durable evidence cannot prove whether the operation committed.</summary>
    Ambiguous = 6,
}
