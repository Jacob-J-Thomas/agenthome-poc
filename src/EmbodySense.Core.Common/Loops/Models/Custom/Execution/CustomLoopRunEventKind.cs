namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Identifies the supported custom loop run event kind values.
/// </summary>
public enum CustomLoopRunEventKind
{
    /// <summary>
    /// Identifies the unknown custom loop run event kind.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the admitted custom loop run event kind.
    /// </summary>
    Admitted = 1,
    /// <summary>
    /// Identifies the lifecycle changed custom loop run event kind.
    /// </summary>
    LifecycleChanged = 2,
    /// <summary>
    /// Identifies the iteration started custom loop run event kind.
    /// </summary>
    IterationStarted = 3,
    /// <summary>
    /// Identifies the node attempt started custom loop run event kind.
    /// </summary>
    NodeAttemptStarted = 4,
    /// <summary>
    /// Identifies the node attempt completed custom loop run event kind.
    /// </summary>
    NodeAttemptCompleted = 5,
    /// <summary>
    /// Identifies the node outcome observed custom loop run event kind.
    /// </summary>
    NodeOutcomeObserved = 6,
    /// <summary>
    /// Identifies the node attempt failed custom loop run event kind.
    /// </summary>
    NodeAttemptFailed = 7,
    /// <summary>
    /// Identifies the exit decision started custom loop run event kind.
    /// </summary>
    ExitDecisionStarted = 8,
    /// <summary>
    /// Identifies the exit decision completed custom loop run event kind.
    /// </summary>
    ExitDecisionCompleted = 9,
    /// <summary>
    /// Identifies the conversation publication started custom loop run event kind.
    /// </summary>
    ConversationPublicationStarted = 10,
    /// <summary>
    /// Identifies the conversation published custom loop run event kind.
    /// </summary>
    ConversationPublished = 11,
    /// <summary>
    /// Identifies the checkpoint committed custom loop run event kind.
    /// </summary>
    CheckpointCommitted = 12,
    /// <summary>
    /// Identifies the integrity warning custom loop run event kind.
    /// </summary>
    IntegrityWarning = 13,
    /// <summary>
    /// Identifies the admission audit completed custom loop run event kind.
    /// </summary>
    AdmissionAuditCompleted = 14,
    /// <summary>
    /// Identifies the tool request reserved custom loop run event kind.
    /// </summary>
    ToolRequestReserved = 15,
    /// <summary>
    /// Identifies the tool governance decided custom loop run event kind.
    /// </summary>
    ToolGovernanceDecided = 16,
    /// <summary>
    /// Identifies the tool outcome observed custom loop run event kind.
    /// </summary>
    ToolOutcomeObserved = 17,
    /// <summary>
    /// Identifies the tool integrity failed custom loop run event kind.
    /// </summary>
    ToolIntegrityFailed = 18
}
