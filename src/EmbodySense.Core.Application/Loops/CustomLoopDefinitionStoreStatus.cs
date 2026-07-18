namespace EmbodySense.Core.Application.Loops;

public enum CustomLoopDefinitionStoreStatus
{
    Unknown = 0,
    Created = 1,
    Updated = 2,
    Deleted = 3,
    Conflict = 4,
    NotFound = 5,
    LimitExceeded = 6,
    AlreadyDeleted = 7,
    AlreadyCreated = 8,
    OperationConflict = 9
}
