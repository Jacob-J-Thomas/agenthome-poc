namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Identifies the durable terminal or blocked outcome of a receipt cleanup journal.
/// </summary>
public enum CustomLoopReceiptCleanupOutcome
{
    /// <summary>
    /// No terminal outcome exists yet.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Selected raw artifacts were replaced with compact proof successfully.
    /// </summary>
    Succeeded,

    /// <summary>
    /// No safe expired candidates existed.
    /// </summary>
    NothingEligible,

    /// <summary>
    /// Candidate concurrency prevented attribution to this batch.
    /// </summary>
    Conflict,

    /// <summary>
    /// Required audit evidence could not be recorded.
    /// </summary>
    AuditUnavailable,

    /// <summary>
    /// Corrupt evidence prevented safe classification or mutation.
    /// </summary>
    Corrupt,

    /// <summary>
    /// Ambiguous or incomplete recovery evidence requires intervention.
    /// </summary>
    Degraded
}
