namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public enum CustomLoopContextSource
{
    Unknown = 0,
    HarnessGovernance = 1,
    RoleInstruction = 2,
    ContextualState = 3,
    RunMetadata = 4,
    NodeInstruction = 5,
    TriggerPrompt = 6,
    InvokingConversation = 7,
    EarlierRetainedOutput = 8,
    PreviousIterationResult = 9,
    AgentIdentity = 10
}
