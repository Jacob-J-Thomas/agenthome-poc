namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

/// <summary>
/// Identifies the supported custom loop trace deletion integrity values.
/// </summary>
public enum CustomLoopTraceDeletionIntegrity
{
    /// <summary>
    /// Identifies the unknown custom loop trace deletion integrity.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the pending outcome audit custom loop trace deletion integrity.
    /// </summary>
    PendingOutcomeAudit = 1,
    /// <summary>
    /// Identifies the outcome audit started custom loop trace deletion integrity.
    /// </summary>
    OutcomeAuditStarted = 2,
    /// <summary>
    /// Identifies the complete custom loop trace deletion integrity.
    /// </summary>
    Complete = 3,
    /// <summary>
    /// Identifies the committed with audit warning custom loop trace deletion integrity.
    /// </summary>
    CommittedWithAuditWarning = 4
}
