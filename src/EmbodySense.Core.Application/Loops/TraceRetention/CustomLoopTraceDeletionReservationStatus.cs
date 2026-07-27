namespace EmbodySense.Core.Application.Loops.TraceRetention;

public enum CustomLoopTraceDeletionReservationStatus
{
    Reserved = 1,
    Pending = 2,
    OutcomeCommitted = 3,
    OperationConflict = 4,
    DeletionOperationLimitExceeded = 5
}
