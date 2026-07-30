namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Selects which admitted and retained sources are composed into a node's inference context.
/// </summary>
/// <param name="IncludeRoleContext">Whether admitted role and agent-identity instructions are included.</param>
/// <param name="IncludeTriggerPrompt">Whether the resolved trigger prompt is included.</param>
/// <param name="IncludeInvokingConversation">Whether the bounded admitted invoking conversation is included.</param>
/// <param name="IncludeEarlierRetainedOutputs">Whether retained outputs from earlier steps are included.</param>
/// <param name="IncludePreviousIterationResult">Whether the preceding iteration's result is included.</param>
public sealed record LoopContextInputPolicy(
    bool IncludeRoleContext,
    bool IncludeTriggerPrompt,
    bool IncludeInvokingConversation,
    bool IncludeEarlierRetainedOutputs,
    bool IncludePreviousIterationResult);
