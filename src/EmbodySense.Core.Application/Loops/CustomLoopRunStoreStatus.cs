namespace EmbodySense.Core.Application.Loops;

public enum CustomLoopRunStoreStatus
{
    Unknown = 0,
    Created = 1,
    Updated = 2,
    AlreadyCreated = 3,
    Conflict = 4,
    OperationConflict = 5,
    NonterminalRunExists = 6,
    NotFound = 7,
    LimitExceeded = 8,
    TerminalImmutable = 9,
    DeletedIdentityConflict = 10
}
