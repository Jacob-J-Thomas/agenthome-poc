using EmbodySense.Core.Application.Loops.TraceRetention.Models;
namespace EmbodySense.Core.Application.Loops.TraceRetention;

/// <summary>
/// Represents a custom loop trace deletion result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Tombstone">The tombstone.</param>
/// <param name="Detail">The detail.</param>
/// <param name="IsOutcomeCommitted">The is outcome committed.</param>
public sealed record CustomLoopTraceDeletionResult(
    CustomLoopTraceDeletionStatus Status,
    CustomLoopTraceTombstone? Tombstone,
    string Detail,
    bool IsOutcomeCommitted)
{
    /// <summary>
    /// Gets a value indicating whether the value is committed.
    /// </summary>
    /// <value><see langword="true"/> when the value is committed; otherwise, <see langword="false"/>.</value>
    public bool IsCommitted => Status is CustomLoopTraceDeletionStatus.Deleted or CustomLoopTraceDeletionStatus.Replayed or CustomLoopTraceDeletionStatus.CommittedWithAuditWarning;
}
