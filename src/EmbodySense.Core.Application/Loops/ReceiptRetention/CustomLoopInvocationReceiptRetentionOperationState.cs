namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

public enum CustomLoopInvocationReceiptRetentionOperationState
{
    Reserved = 1,
    IntentAuditRecorded = 2,
    OutcomeCommitted = 3,
    OutcomeAuditStarted = 4,
    OutcomeAuditRecorded = 5,
    CommittedWithAuditWarning = 6,
    AbandonedCandidateChanged = 7
}
