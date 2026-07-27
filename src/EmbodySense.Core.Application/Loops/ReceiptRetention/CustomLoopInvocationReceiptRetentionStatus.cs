namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

public enum CustomLoopInvocationReceiptRetentionStatus
{
    Pruned = 1,
    Replayed = 2,
    NothingEligible = 3,
    OperationInProgress = 4,
    AuditUnavailable = 5,
    CommittedWithAuditWarning = 6,
    Invalid = 7
}
