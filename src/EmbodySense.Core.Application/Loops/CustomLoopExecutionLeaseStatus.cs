namespace EmbodySense.Core.Application.Loops;

public enum CustomLoopExecutionLeaseStatus
{
    Unknown = 0,
    Acquired = 1,
    WorkspaceBusy = 2,
    OperationInProgress = 3,
    OperationConflict = 4
}
