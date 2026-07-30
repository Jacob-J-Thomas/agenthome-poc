namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop execution lease status values.
/// </summary>
public enum CustomLoopExecutionLeaseStatus
{
    /// <summary>
    /// Identifies the acquired custom loop execution lease status.
    /// </summary>
    Acquired = 1,
    /// <summary>
    /// Identifies the workspace busy custom loop execution lease status.
    /// </summary>
    WorkspaceBusy = 2,
    /// <summary>
    /// Identifies the operation in progress custom loop execution lease status.
    /// </summary>
    OperationInProgress = 3,
    /// <summary>
    /// Identifies the operation conflict custom loop execution lease status.
    /// </summary>
    OperationConflict = 4,
    /// <summary>
    /// Identifies the busy outcome reserved custom loop execution lease status.
    /// </summary>
    BusyOutcomeReserved = 5,
    /// <summary>
    /// Identifies the workspace available custom loop execution lease status.
    /// </summary>
    WorkspaceAvailable = 6,
    /// <summary>
    /// Identifies the workspace host unavailable custom loop execution lease status.
    /// </summary>
    WorkspaceHostUnavailable = 7
}
