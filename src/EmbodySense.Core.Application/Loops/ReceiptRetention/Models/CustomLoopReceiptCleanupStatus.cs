namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Identifies the externally visible result of governed receipt cleanup.
/// </summary>
public enum CustomLoopReceiptCleanupStatus
{
    /// <summary>
    /// No result status was supplied.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A bounded batch was compacted and audited.
    /// </summary>
    Pruned,

    /// <summary>
    /// The same cleanup operation and outcome were replayed.
    /// </summary>
    Replayed,

    /// <summary>
    /// No safe expired candidate was available.
    /// </summary>
    NothingEligible,

    /// <summary>
    /// Another process owns the bounded cleanup or recovery window.
    /// </summary>
    OperationInProgress,

    /// <summary>
    /// Raw artifact, compact proof, reserved, or workspace capacity is exhausted.
    /// </summary>
    QuotaExhausted,

    /// <summary>
    /// Required intent or outcome auditing is unavailable.
    /// </summary>
    AuditUnavailable,

    /// <summary>
    /// A selected candidate changed or disappeared after durable intent.
    /// </summary>
    CleanupConflict,

    /// <summary>
    /// Corrupt evidence prevented safe cleanup.
    /// </summary>
    Corrupt,

    /// <summary>
    /// Ambiguous recovery evidence requires explicit intervention.
    /// </summary>
    Degraded,

    /// <summary>
    /// The request or durable journal is invalid.
    /// </summary>
    Invalid,

    /// <summary>
    /// Artifacts were compacted but the bounded outcome-audit attempt requires review.
    /// </summary>
    CommittedWithAuditWarning
}
