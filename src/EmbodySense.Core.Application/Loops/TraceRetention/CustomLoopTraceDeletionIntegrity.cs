namespace EmbodySense.Core.Application.Loops.TraceRetention;

public enum CustomLoopTraceDeletionIntegrity
{
    Unknown = 0,
    PendingOutcomeAudit = 1,
    OutcomeAuditStarted = 2,
    Complete = 3,
    CommittedWithAuditWarning = 4
}
