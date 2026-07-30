namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Identifies the supported custom loop recovery status values.
/// </summary>
public enum CustomLoopRecoveryStatus
{
    /// <summary>
    /// Identifies the unchanged custom loop recovery status.
    /// </summary>
    Unchanged = 1,
    /// <summary>
    /// Identifies the paused custom loop recovery status.
    /// </summary>
    Paused = 2,
    /// <summary>
    /// Identifies the cancelled custom loop recovery status.
    /// </summary>
    Cancelled = 3,
    /// <summary>
    /// Identifies the needs review custom loop recovery status.
    /// </summary>
    NeedsReview = 4,
    /// <summary>
    /// Identifies the conflict custom loop recovery status.
    /// </summary>
    Conflict = 5,
    /// <summary>
    /// Identifies the failed custom loop recovery status.
    /// </summary>
    Failed = 6
}
