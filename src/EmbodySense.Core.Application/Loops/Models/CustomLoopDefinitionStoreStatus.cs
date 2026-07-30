namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop definition store status values.
/// </summary>
public enum CustomLoopDefinitionStoreStatus
{
    /// <summary>
    /// Identifies the unknown custom loop definition store status.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the created custom loop definition store status.
    /// </summary>
    Created = 1,
    /// <summary>
    /// Identifies the updated custom loop definition store status.
    /// </summary>
    Updated = 2,
    /// <summary>
    /// Identifies the deleted custom loop definition store status.
    /// </summary>
    Deleted = 3,
    /// <summary>
    /// Identifies the conflict custom loop definition store status.
    /// </summary>
    Conflict = 4,
    /// <summary>
    /// Identifies the not found custom loop definition store status.
    /// </summary>
    NotFound = 5,
    /// <summary>
    /// Identifies the limit exceeded custom loop definition store status.
    /// </summary>
    LimitExceeded = 6,
    /// <summary>
    /// Identifies the already deleted custom loop definition store status.
    /// </summary>
    AlreadyDeleted = 7,
    /// <summary>
    /// Identifies the already created custom loop definition store status.
    /// </summary>
    AlreadyCreated = 8,
    /// <summary>
    /// Identifies the operation conflict custom loop definition store status.
    /// </summary>
    OperationConflict = 9
}
