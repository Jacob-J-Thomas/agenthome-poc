namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

/// <summary>
/// Identifies the supported custom loop trace deletion status values.
/// </summary>
public enum CustomLoopTraceDeletionStatus
{
    /// <summary>
    /// Identifies the deleted custom loop trace deletion status.
    /// </summary>
    Deleted = 1,
    /// <summary>
    /// Identifies the replayed custom loop trace deletion status.
    /// </summary>
    Replayed = 2,
    /// <summary>
    /// Identifies the not found custom loop trace deletion status.
    /// </summary>
    NotFound = 3,
    /// <summary>
    /// Identifies the nonterminal custom loop trace deletion status.
    /// </summary>
    Nonterminal = 4,
    /// <summary>
    /// Identifies the hash mismatch custom loop trace deletion status.
    /// </summary>
    HashMismatch = 5,
    /// <summary>
    /// Identifies the conflict custom loop trace deletion status.
    /// </summary>
    Conflict = 6,
    /// <summary>
    /// Identifies the limit exceeded custom loop trace deletion status.
    /// </summary>
    LimitExceeded = 7,
    /// <summary>
    /// Identifies the invalid custom loop trace deletion status.
    /// </summary>
    Invalid = 8,
    /// <summary>
    /// Identifies the audit unavailable custom loop trace deletion status.
    /// </summary>
    AuditUnavailable = 9,
    /// <summary>
    /// Identifies the committed with audit warning custom loop trace deletion status.
    /// </summary>
    CommittedWithAuditWarning = 10,
    /// <summary>
    /// Identifies the operation limit exceeded custom loop trace deletion status.
    /// </summary>
    OperationLimitExceeded = 11,
    /// <summary>
    /// Identifies the operation in progress custom loop trace deletion status.
    /// </summary>
    OperationInProgress = 12
}
