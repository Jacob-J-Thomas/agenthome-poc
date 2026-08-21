namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Identifies the supported custom loop run status values.
/// </summary>
public enum CustomLoopRunStatus
{
    /// <summary>
    /// Identifies the unknown custom loop run status.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the admitted custom loop run status.
    /// </summary>
    Admitted = 1,
    /// <summary>
    /// Identifies the running custom loop run status.
    /// </summary>
    Running = 2,
    /// <summary>
    /// Identifies the pause requested custom loop run status.
    /// </summary>
    PauseRequested = 3,
    /// <summary>
    /// Identifies the paused custom loop run status.
    /// </summary>
    Paused = 4,
    /// <summary>
    /// Identifies the cancel requested custom loop run status.
    /// </summary>
    CancelRequested = 5,
    /// <summary>
    /// Identifies the completed custom loop run status.
    /// </summary>
    Completed = 6,
    /// <summary>
    /// Identifies the failed custom loop run status.
    /// </summary>
    Failed = 7,
    /// <summary>
    /// Identifies the cancelled custom loop run status.
    /// </summary>
    Cancelled = 8,
    /// <summary>
    /// Identifies the needs review custom loop run status.
    /// </summary>
    NeedsReview = 9,
    /// <summary>
    /// Identifies a run durably waiting for one admitted wake condition.
    /// </summary>
    Waiting = 10
}
