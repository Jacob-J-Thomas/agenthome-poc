namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

public sealed record CustomLoopTraceDeletionResult(
    CustomLoopTraceDeletionStatus Status,
    CustomLoopTraceTombstone? Tombstone,
    string Detail,
    bool IsOutcomeCommitted)
{
    public bool IsCommitted => Status is CustomLoopTraceDeletionStatus.Deleted or CustomLoopTraceDeletionStatus.Replayed or CustomLoopTraceDeletionStatus.CommittedWithAuditWarning;
}
