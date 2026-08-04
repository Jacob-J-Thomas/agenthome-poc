namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Identifies the durable stage of a crash-recoverable receipt cleanup journal.
/// </summary>
public enum CustomLoopReceiptCleanupStage
{
    /// <summary>
    /// No stage was supplied.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Immutable candidates and ownership are durably journaled before mutation.
    /// </summary>
    IntentPersisted,

    /// <summary>
    /// The cleanup intent audit is durably recorded.
    /// </summary>
    IntentAuditRecorded,

    /// <summary>
    /// The replacement compact proof ledger is durably written and verified.
    /// </summary>
    ProofLedgerWritten,

    /// <summary>
    /// Every selected raw artifact was removed after hash revalidation.
    /// </summary>
    ArtifactsRemoved,

    /// <summary>
    /// The single bounded outcome-audit attempt was durably started.
    /// </summary>
    OutcomeAuditStarted,

    /// <summary>
    /// Cleanup and its outcome audit are durably complete.
    /// </summary>
    Completed,

    /// <summary>
    /// Cleanup committed but its bounded outcome-audit attempt requires review.
    /// </summary>
    CommittedWithAuditWarning,

    /// <summary>
    /// A candidate changed or disappeared and the batch was abandoned without attribution.
    /// </summary>
    AbandonedConflict,

    /// <summary>
    /// Recovery cannot advance safely without explicit intervention.
    /// </summary>
    Degraded
}
