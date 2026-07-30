namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop control status values.
/// </summary>
public enum CustomLoopControlStatus
{
    /// <summary>
    /// Identifies the unknown custom loop control status.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the pause requested custom loop control status.
    /// </summary>
    PauseRequested = 1,
    /// <summary>
    /// Identifies the paused custom loop control status.
    /// </summary>
    Paused = 2,
    /// <summary>
    /// Identifies the cancel requested custom loop control status.
    /// </summary>
    CancelRequested = 3,
    /// <summary>
    /// Identifies the cancelled custom loop control status.
    /// </summary>
    Cancelled = 4,
    /// <summary>
    /// Identifies the resumed custom loop control status.
    /// </summary>
    Resumed = 5,
    /// <summary>
    /// Identifies the completed custom loop control status.
    /// </summary>
    Completed = 6,
    /// <summary>
    /// Identifies the failed custom loop control status.
    /// </summary>
    Failed = 7,
    /// <summary>
    /// Identifies the needs review custom loop control status.
    /// </summary>
    NeedsReview = 8,
    /// <summary>
    /// Identifies the conflict custom loop control status.
    /// </summary>
    Conflict = 10,
    /// <summary>
    /// Identifies the invalid state custom loop control status.
    /// </summary>
    InvalidState = 11,
    /// <summary>
    /// Identifies the not found custom loop control status.
    /// </summary>
    NotFound = 12,
    /// <summary>
    /// Identifies the audit warning custom loop control status.
    /// </summary>
    AuditWarning = 13,
    /// <summary>
    /// Identifies the workspace execution busy custom loop control status.
    /// </summary>
    WorkspaceExecutionBusy = 14,
    /// <summary>
    /// Identifies the operation in progress custom loop control status.
    /// </summary>
    OperationInProgress = 15,
    /// <summary>
    /// Identifies the workspace host unavailable custom loop control status.
    /// </summary>
    WorkspaceHostUnavailable = 16
}
