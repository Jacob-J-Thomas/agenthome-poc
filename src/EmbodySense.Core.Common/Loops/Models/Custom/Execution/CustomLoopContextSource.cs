namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Identifies the supported custom loop context source values.
/// </summary>
public enum CustomLoopContextSource
{
    /// <summary>
    /// Identifies the unknown custom loop context source.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the harness governance custom loop context source.
    /// </summary>
    HarnessGovernance = 1,
    /// <summary>
    /// Identifies the role instruction custom loop context source.
    /// </summary>
    RoleInstruction = 2,
    /// <summary>
    /// Identifies the contextual state custom loop context source.
    /// </summary>
    ContextualState = 3,
    /// <summary>
    /// Identifies the run metadata custom loop context source.
    /// </summary>
    RunMetadata = 4,
    /// <summary>
    /// Identifies the node instruction custom loop context source.
    /// </summary>
    NodeInstruction = 5,
    /// <summary>
    /// Identifies the trigger prompt custom loop context source.
    /// </summary>
    TriggerPrompt = 6,
    /// <summary>
    /// Identifies the invoking conversation custom loop context source.
    /// </summary>
    InvokingConversation = 7,
    /// <summary>
    /// Identifies the earlier retained output custom loop context source.
    /// </summary>
    EarlierRetainedOutput = 8,
    /// <summary>
    /// Identifies the previous iteration result custom loop context source.
    /// </summary>
    PreviousIterationResult = 9,
    /// <summary>
    /// Identifies the agent identity custom loop context source.
    /// </summary>
    AgentIdentity = 10
}
