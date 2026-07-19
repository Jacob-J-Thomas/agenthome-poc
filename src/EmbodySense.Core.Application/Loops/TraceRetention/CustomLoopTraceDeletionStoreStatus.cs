namespace EmbodySense.Core.Application.Loops.TraceRetention;

public enum CustomLoopTraceDeletionStoreStatus
{
    Unknown = 0,
    Deleted = 1,
    AlreadyDeleted = 2,
    NotFound = 3,
    Nonterminal = 4,
    HashMismatch = 5,
    OperationConflict = 6,
    TombstoneLimitExceeded = 7
}
