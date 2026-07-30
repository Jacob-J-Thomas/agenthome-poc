namespace EmbodySense.Core.Common.Loops.Models;

/// <summary>
/// Identifies the supported loop run status values.
/// </summary>
public enum LoopRunStatus
{
    /// <summary>
    /// Identifies the unknown loop run status.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the started loop run status.
    /// </summary>
    Started,
    /// <summary>
    /// Identifies the completed loop run status.
    /// </summary>
    Completed,
    /// <summary>
    /// Identifies the failed loop run status.
    /// </summary>
    Failed,
    /// <summary>
    /// Identifies the cancelled loop run status.
    /// </summary>
    Cancelled
}
