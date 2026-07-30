namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Identifies the supported custom loop invocation receipt retention status values.
/// </summary>
public enum CustomLoopInvocationReceiptRetentionStatus
{
    /// <summary>
    /// Identifies the pruned custom loop invocation receipt retention status.
    /// </summary>
    Pruned = 1,
    /// <summary>
    /// Identifies the replayed custom loop invocation receipt retention status.
    /// </summary>
    Replayed = 2,
    /// <summary>
    /// Identifies the nothing eligible custom loop invocation receipt retention status.
    /// </summary>
    NothingEligible = 3,
    /// <summary>
    /// Identifies the operation in progress custom loop invocation receipt retention status.
    /// </summary>
    OperationInProgress = 4,
    /// <summary>
    /// Identifies the audit unavailable custom loop invocation receipt retention status.
    /// </summary>
    AuditUnavailable = 5,
    /// <summary>
    /// Identifies the committed with audit warning custom loop invocation receipt retention status.
    /// </summary>
    CommittedWithAuditWarning = 6,
    /// <summary>
    /// Identifies the invalid custom loop invocation receipt retention status.
    /// </summary>
    Invalid = 7
}
