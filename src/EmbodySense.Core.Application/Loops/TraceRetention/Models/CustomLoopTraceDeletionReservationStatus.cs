namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

/// <summary>
/// Identifies the supported custom loop trace deletion reservation status values.
/// </summary>
public enum CustomLoopTraceDeletionReservationStatus
{
    /// <summary>
    /// Identifies the reserved custom loop trace deletion reservation status.
    /// </summary>
    Reserved = 1,
    /// <summary>
    /// Identifies the pending custom loop trace deletion reservation status.
    /// </summary>
    Pending = 2,
    /// <summary>
    /// Identifies the outcome committed custom loop trace deletion reservation status.
    /// </summary>
    OutcomeCommitted = 3,
    /// <summary>
    /// Identifies the operation conflict custom loop trace deletion reservation status.
    /// </summary>
    OperationConflict = 4,
    /// <summary>
    /// Identifies the deletion operation limit exceeded custom loop trace deletion reservation status.
    /// </summary>
    DeletionOperationLimitExceeded = 5
}
