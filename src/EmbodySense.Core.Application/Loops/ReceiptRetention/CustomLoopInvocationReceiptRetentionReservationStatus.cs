namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

public enum CustomLoopInvocationReceiptRetentionReservationStatus
{
    Reserved = 1,
    ReadyToCommit = 2,
    OutcomeCommitted = 3,
    OperationInProgress = 4,
    NothingEligible = 5
}
