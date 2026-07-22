namespace EmbodySense.Core.Application.Loops;

public enum CustomLoopExecutionLeaseStatus
{
    Acquired = 1,
    WorkspaceBusy = 2,
    OperationInProgress = 3,
    OperationConflict = 4,
    BusyOutcomeReserved = 5,
    WorkspaceAvailable = 6,
    WorkspaceHostUnavailable = 7
}
