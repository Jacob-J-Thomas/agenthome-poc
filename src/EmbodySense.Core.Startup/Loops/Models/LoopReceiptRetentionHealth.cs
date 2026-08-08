namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Identifies the safe, actionable health projection for custom-loop receipt retention.
/// </summary>
public enum LoopReceiptRetentionHealth
{
    /// <summary>
    /// Retention is within its bounded posture and has no known cleanup blocker.
    /// </summary>
    Healthy,

    /// <summary>
    /// A receipt, proof, history, or workspace capacity ceiling is exhausted.
    /// </summary>
    Exhausted,

    /// <summary>
    /// Strict evidence validation failed closed.
    /// </summary>
    Corrupt,

    /// <summary>
    /// A required governed audit append is unavailable.
    /// </summary>
    AuditUnavailable,

    /// <summary>
    /// Another process owns a cleanup operation.
    /// </summary>
    OwnershipConflict,

    /// <summary>
    /// Retained evidence is ambiguous, conflicted, or otherwise requires review.
    /// </summary>
    Degraded,

    /// <summary>
    /// A durable cleanup journal remains active inside its bounded recovery window.
    /// </summary>
    RecoveryPending
}
