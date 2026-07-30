namespace EmbodySense.Core.Application.Loops.Authoring.Models;

/// <summary>
/// Identifies the supported custom loop authoring status values.
/// </summary>
public enum CustomLoopAuthoringStatus
{
    /// <summary>
    /// Identifies the created custom loop authoring status.
    /// </summary>
    Created = 1,
    /// <summary>
    /// Identifies the updated custom loop authoring status.
    /// </summary>
    Updated = 2,
    /// <summary>
    /// Identifies the deleted custom loop authoring status.
    /// </summary>
    Deleted = 3,
    /// <summary>
    /// Identifies the replayed custom loop authoring status.
    /// </summary>
    Replayed = 4,
    /// <summary>
    /// Identifies the invalid custom loop authoring status.
    /// </summary>
    Invalid = 5,
    /// <summary>
    /// Identifies the conflict custom loop authoring status.
    /// </summary>
    Conflict = 6,
    /// <summary>
    /// Identifies the not found custom loop authoring status.
    /// </summary>
    NotFound = 7,
    /// <summary>
    /// Identifies the limit exceeded custom loop authoring status.
    /// </summary>
    LimitExceeded = 8,
    /// <summary>
    /// Identifies the audit unavailable custom loop authoring status.
    /// </summary>
    AuditUnavailable = 9,
    /// <summary>
    /// Identifies the committed with audit warning custom loop authoring status.
    /// </summary>
    CommittedWithAuditWarning = 10,
    /// <summary>
    /// Identifies the active run exists custom loop authoring status.
    /// </summary>
    ActiveRunExists = 11
}
