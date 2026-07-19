namespace EmbodySense.Core.Application.Loops.TraceRetention;

public sealed record CustomLoopTraceDeletionStoreResult(
    CustomLoopTraceDeletionStoreStatus Status,
    CustomLoopTraceTombstone? Tombstone,
    CustomLoopTraceDeletionIntegrity Integrity)
{
    public bool IsCommitted => Status is CustomLoopTraceDeletionStoreStatus.Deleted or CustomLoopTraceDeletionStoreStatus.AlreadyDeleted;
}
