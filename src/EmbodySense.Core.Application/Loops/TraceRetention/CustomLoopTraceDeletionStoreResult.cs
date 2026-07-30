using EmbodySense.Core.Application.Loops.TraceRetention.Models;
namespace EmbodySense.Core.Application.Loops.TraceRetention;

/// <summary>
/// Represents a custom loop trace deletion store result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Tombstone">The tombstone.</param>
/// <param name="Integrity">The integrity.</param>
public sealed record CustomLoopTraceDeletionStoreResult(
    CustomLoopTraceDeletionStoreStatus Status,
    CustomLoopTraceTombstone? Tombstone,
    CustomLoopTraceDeletionIntegrity Integrity)
{
    /// <summary>
    /// Gets a value indicating whether the value is committed.
    /// </summary>
    /// <value><see langword="true"/> when the value is committed; otherwise, <see langword="false"/>.</value>
    public bool IsCommitted => Status is CustomLoopTraceDeletionStoreStatus.Deleted or CustomLoopTraceDeletionStoreStatus.AlreadyDeleted;

    /// <summary>
    /// Gets a value indicating whether the value has committed outcome.
    /// </summary>
    /// <value><see langword="true"/> when the value has committed outcome; otherwise, <see langword="false"/>.</value>
    public bool HasCommittedOutcome => Integrity != CustomLoopTraceDeletionIntegrity.Unknown
        && Status is not CustomLoopTraceDeletionStoreStatus.Unknown and not CustomLoopTraceDeletionStoreStatus.DeletionOperationLimitExceeded;
}
