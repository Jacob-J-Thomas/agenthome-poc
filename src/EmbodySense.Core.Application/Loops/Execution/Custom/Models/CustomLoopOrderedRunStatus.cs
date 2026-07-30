namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Identifies the supported custom loop ordered run status values.
/// </summary>
public enum CustomLoopOrderedRunStatus
{
    /// <summary>
    /// Identifies the completed custom loop ordered run status.
    /// </summary>
    Completed = 1,
    /// <summary>
    /// Identifies the failed custom loop ordered run status.
    /// </summary>
    Failed = 2,
    /// <summary>
    /// Identifies the needs review custom loop ordered run status.
    /// </summary>
    NeedsReview = 3,
    /// <summary>
    /// Identifies the conflict custom loop ordered run status.
    /// </summary>
    Conflict = 4,
    /// <summary>
    /// Identifies the invalid state custom loop ordered run status.
    /// </summary>
    InvalidState = 5,
    /// <summary>
    /// Identifies the not found custom loop ordered run status.
    /// </summary>
    NotFound = 6,
    /// <summary>
    /// Identifies the cancelled custom loop ordered run status.
    /// </summary>
    Cancelled = 7,
    /// <summary>
    /// Identifies the paused custom loop ordered run status.
    /// </summary>
    Paused = 8
}
