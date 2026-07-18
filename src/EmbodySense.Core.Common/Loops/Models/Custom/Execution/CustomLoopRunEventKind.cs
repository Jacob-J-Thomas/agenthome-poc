namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public enum CustomLoopRunEventKind
{
    Unknown = 0,
    Admitted = 1,
    LifecycleChanged = 2,
    IterationStarted = 3,
    NodeAttemptStarted = 4,
    NodeAttemptCompleted = 5,
    NodeOutcomeObserved = 6,
    NodeAttemptFailed = 7,
    ExitDecisionStarted = 8,
    ExitDecisionCompleted = 9,
    ConversationPublicationStarted = 10,
    ConversationPublished = 11,
    CheckpointCommitted = 12,
    IntegrityWarning = 13,
    AdmissionAuditCompleted = 14,
    ToolRequestReserved = 15,
    ToolGovernanceDecided = 16,
    ToolOutcomeObserved = 17,
    ToolIntegrityFailed = 18
}
