namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop invocation operation store status values.
/// </summary>
public enum CustomLoopInvocationOperationStoreStatus
{
    /// <summary>
    /// Identifies the created custom loop invocation operation store status.
    /// </summary>
    Created = 1,
    /// <summary>
    /// Identifies the replayed custom loop invocation operation store status.
    /// </summary>
    Replayed = 2,
    /// <summary>
    /// Identifies the conflict custom loop invocation operation store status.
    /// </summary>
    Conflict = 3,
    /// <summary>
    /// Identifies the completed custom loop invocation operation store status.
    /// </summary>
    Completed = 4,
    /// <summary>
    /// Identifies the not found custom loop invocation operation store status.
    /// </summary>
    NotFound = 5,
    /// <summary>
    /// Identifies the bound custom loop invocation operation store status.
    /// </summary>
    Bound = 6,
    /// <summary>
    /// Identifies the limit exceeded custom loop invocation operation store status.
    /// </summary>
    LimitExceeded = 7,
    /// <summary>
    /// Identifies the retention required custom loop invocation operation store status.
    /// </summary>
    RetentionRequired = 8,
    /// <summary>
    /// Identifies the retention audit unavailable custom loop invocation operation store status.
    /// </summary>
    RetentionAuditUnavailable = 9,
    /// <summary>
    /// Identifies the retention invalid custom loop invocation operation store status.
    /// </summary>
    RetentionInvalid = 10
}
