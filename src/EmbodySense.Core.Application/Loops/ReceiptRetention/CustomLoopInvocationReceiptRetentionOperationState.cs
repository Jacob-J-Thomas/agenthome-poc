namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

public enum CustomLoopInvocationReceiptRetentionOperationState
{
    Reserved = 1,
    IntentAuditRecorded = 2,
    OutcomeCommitted = 3,
    OutcomeAuditRecorded = 4
}
