namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Identifies the supported custom loop invocation receipt retention operation state values.
/// </summary>
public enum CustomLoopInvocationReceiptRetentionOperationState
{
    /// <summary>
    /// Identifies the reserved custom loop invocation receipt retention operation state.
    /// </summary>
    Reserved = 1,
    /// <summary>
    /// Identifies the intent audit recorded custom loop invocation receipt retention operation state.
    /// </summary>
    IntentAuditRecorded = 2,
    /// <summary>
    /// Identifies the outcome committed custom loop invocation receipt retention operation state.
    /// </summary>
    OutcomeCommitted = 3,
    /// <summary>
    /// Identifies the outcome audit started custom loop invocation receipt retention operation state.
    /// </summary>
    OutcomeAuditStarted = 4,
    /// <summary>
    /// Identifies the outcome audit recorded custom loop invocation receipt retention operation state.
    /// </summary>
    OutcomeAuditRecorded = 5,
    /// <summary>
    /// Identifies the committed with audit warning custom loop invocation receipt retention operation state.
    /// </summary>
    CommittedWithAuditWarning = 6,
    /// <summary>
    /// Identifies the abandoned candidate changed custom loop invocation receipt retention operation state.
    /// </summary>
    AbandonedCandidateChanged = 7,
    /// <summary>
    /// Identifies the abandoned conflict audit started custom loop invocation receipt retention operation state.
    /// </summary>
    AbandonedConflictAuditStarted = 8,
    /// <summary>
    /// Identifies the abandoned conflict audit recorded custom loop invocation receipt retention operation state.
    /// </summary>
    AbandonedConflictAuditRecorded = 9,
    /// <summary>
    /// Identifies the abandoned with audit warning custom loop invocation receipt retention operation state.
    /// </summary>
    AbandonedWithAuditWarning = 10
}
