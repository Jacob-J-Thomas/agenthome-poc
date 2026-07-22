namespace EmbodySense.Core.Application.Loops;

public enum CustomLoopControlOperationStoreStatus
{
    Created = 1,
    Replayed = 2,
    Conflict = 3,
    Completed = 4,
    NotFound = 5
}
