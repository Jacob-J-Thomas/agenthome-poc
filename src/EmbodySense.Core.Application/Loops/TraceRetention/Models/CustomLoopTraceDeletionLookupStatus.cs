namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

/// <summary>
/// Identifies the supported custom loop trace deletion lookup status values.
/// </summary>
public enum CustomLoopTraceDeletionLookupStatus
{
    /// <summary>
    /// Identifies the not found custom loop trace deletion lookup status.
    /// </summary>
    NotFound = 1,
    /// <summary>
    /// Identifies the pending mutation custom loop trace deletion lookup status.
    /// </summary>
    PendingMutation = 2,
    /// <summary>
    /// Identifies the outcome committed custom loop trace deletion lookup status.
    /// </summary>
    OutcomeCommitted = 3
}
