namespace EmbodySense.Core.Application.Loops.Models;

public enum CustomLoopInvocationOperationStoreStatus
{
    Created = 1,
    Replayed = 2,
    Conflict = 3,
    Completed = 4,
    NotFound = 5,
    Bound = 6,
    LimitExceeded = 7,
    RetentionRequired = 8,
    RetentionAuditUnavailable = 9,
    RetentionInvalid = 10
}
