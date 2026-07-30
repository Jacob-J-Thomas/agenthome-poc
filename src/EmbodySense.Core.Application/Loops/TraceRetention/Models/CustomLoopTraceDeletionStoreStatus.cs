namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

/// <summary>
/// Identifies the supported custom loop trace deletion store status values.
/// </summary>
public enum CustomLoopTraceDeletionStoreStatus
{
    /// <summary>
    /// Identifies the unknown custom loop trace deletion store status.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the deleted custom loop trace deletion store status.
    /// </summary>
    Deleted = 1,
    /// <summary>
    /// Identifies the already deleted custom loop trace deletion store status.
    /// </summary>
    AlreadyDeleted = 2,
    /// <summary>
    /// Identifies the not found custom loop trace deletion store status.
    /// </summary>
    NotFound = 3,
    /// <summary>
    /// Identifies the nonterminal custom loop trace deletion store status.
    /// </summary>
    Nonterminal = 4,
    /// <summary>
    /// Identifies the hash mismatch custom loop trace deletion store status.
    /// </summary>
    HashMismatch = 5,
    /// <summary>
    /// Identifies the operation conflict custom loop trace deletion store status.
    /// </summary>
    OperationConflict = 6,
    /// <summary>
    /// Identifies the tombstone limit exceeded custom loop trace deletion store status.
    /// </summary>
    TombstoneLimitExceeded = 7,
    /// <summary>
    /// Identifies the deletion operation limit exceeded custom loop trace deletion store status.
    /// </summary>
    DeletionOperationLimitExceeded = 8,
    /// <summary>
    /// Identifies the audit unavailable custom loop trace deletion store status.
    /// </summary>
    AuditUnavailable = 9
}
