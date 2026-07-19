namespace EmbodySense.Core.Application.Loops.TraceRetention;

public sealed record CustomLoopTraceDeletionResult(
    CustomLoopTraceDeletionStatus Status,
    CustomLoopTraceTombstone? Tombstone,
    string Detail)
{
    public bool IsCommitted => Status is CustomLoopTraceDeletionStatus.Deleted or CustomLoopTraceDeletionStatus.Replayed or CustomLoopTraceDeletionStatus.CommittedWithAuditWarning;
}
