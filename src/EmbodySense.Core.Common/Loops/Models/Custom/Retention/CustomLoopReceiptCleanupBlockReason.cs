namespace EmbodySense.Core.Common.Loops.Models.Custom.Retention;

/// <summary>
/// Identifies why governed receipt cleanup must fail closed.
/// </summary>
public enum CustomLoopReceiptCleanupBlockReason
{
    /// <summary>
    /// Cleanup is not blocked.
    /// </summary>
    None = 0,

    /// <summary>
    /// A pending operation could still change the retained evidence.
    /// </summary>
    PendingEvidence,

    /// <summary>
    /// A terminal outcome is not durably audited.
    /// </summary>
    UnauditedEvidence,

    /// <summary>
    /// Degraded evidence requires explicit review.
    /// </summary>
    DegradedEvidence,

    /// <summary>
    /// Corrupt evidence prevents safe classification.
    /// </summary>
    CorruptEvidence,

    /// <summary>
    /// Cross-process ownership is unresolved.
    /// </summary>
    OwnershipUnresolved,

    /// <summary>
    /// Duplicate or conflicting evidence makes the transition ambiguous.
    /// </summary>
    AmbiguousEvidence,

    /// <summary>
    /// The required audit sink is unavailable.
    /// </summary>
    AuditUnavailable,

    /// <summary>
    /// A cleanup candidate changed after durable intent was recorded.
    /// </summary>
    CleanupConflict,

    /// <summary>
    /// Compact proof has no capacity for the required lineage or idempotency evidence.
    /// </summary>
    ProofCapacityExhausted,

    /// <summary>
    /// Completed cleanup-operation history has no capacity for another immutable identity.
    /// </summary>
    CleanupHistoryCapacityExhausted
}
