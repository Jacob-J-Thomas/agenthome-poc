namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Identifies the supported custom loop invocation receipt retention reservation status values.
/// </summary>
public enum CustomLoopInvocationReceiptRetentionReservationStatus
{
    /// <summary>
    /// Identifies the reserved custom loop invocation receipt retention reservation status.
    /// </summary>
    Reserved = 1,
    /// <summary>
    /// Identifies the ready to commit custom loop invocation receipt retention reservation status.
    /// </summary>
    ReadyToCommit = 2,
    /// <summary>
    /// Identifies the outcome committed custom loop invocation receipt retention reservation status.
    /// </summary>
    OutcomeCommitted = 3,
    /// <summary>
    /// Identifies the operation in progress custom loop invocation receipt retention reservation status.
    /// </summary>
    OperationInProgress = 4,
    /// <summary>
    /// Identifies the nothing eligible custom loop invocation receipt retention reservation status.
    /// </summary>
    NothingEligible = 5,
    /// <summary>
    /// Identifies the conflict pending audit custom loop invocation receipt retention reservation status.
    /// </summary>
    ConflictPendingAudit = 6
}
