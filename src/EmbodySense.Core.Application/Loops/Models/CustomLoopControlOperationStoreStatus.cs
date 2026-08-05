namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop control operation store status values.
/// </summary>
public enum CustomLoopControlOperationStoreStatus
{
    /// <summary>
    /// Identifies the created custom loop control operation store status.
    /// </summary>
    Created = 1,
    /// <summary>
    /// Identifies the replayed custom loop control operation store status.
    /// </summary>
    Replayed = 2,
    /// <summary>
    /// Identifies the conflict custom loop control operation store status.
    /// </summary>
    Conflict = 3,
    /// <summary>
    /// Identifies the completed custom loop control operation store status.
    /// </summary>
    Completed = 4,
    /// <summary>
    /// Identifies the not found custom loop control operation store status.
    /// </summary>
    NotFound = 5,
    /// <summary>
    /// Identifies the ownership unproven custom loop control operation store status.
    /// </summary>
    OwnershipUnproven = 6,
    /// <summary>
    /// Identifies the quota exceeded custom loop control operation store status.
    /// </summary>
    QuotaExceeded = 7,
    /// <summary>
    /// Identifies the expired custom loop control operation store status.
    /// </summary>
    Expired = 8
}
