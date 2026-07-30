namespace EmbodySense.Core.Startup.Loops.Models;

public sealed record LoopContextInputPolicy(
    bool IncludeRoleContext,
    bool IncludeTriggerPrompt,
    bool IncludeInvokingConversation,
    bool IncludeEarlierRetainedOutputs,
    bool IncludePreviousIterationResult);
