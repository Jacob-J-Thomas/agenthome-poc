using EmbodySense.Core.Application.Loops.TraceRetention.Models;
namespace EmbodySense.Core.Application.Loops.TraceRetention;

public sealed record CustomLoopTraceDeletionStoreResult(
    CustomLoopTraceDeletionStoreStatus Status,
    CustomLoopTraceTombstone? Tombstone,
    CustomLoopTraceDeletionIntegrity Integrity)
{
    public bool IsCommitted => Status is CustomLoopTraceDeletionStoreStatus.Deleted or CustomLoopTraceDeletionStoreStatus.AlreadyDeleted;

    public bool HasCommittedOutcome => Integrity != CustomLoopTraceDeletionIntegrity.Unknown
        && Status is not CustomLoopTraceDeletionStoreStatus.Unknown and not CustomLoopTraceDeletionStoreStatus.DeletionOperationLimitExceeded;
}
