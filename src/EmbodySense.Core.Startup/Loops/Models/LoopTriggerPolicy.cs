namespace EmbodySense.Core.Startup.Loops.Models;

public sealed record LoopTriggerPolicy(
    LoopTriggerPromptSource PromptSource,
    string PresetPrompt,
    bool IncludeInvokingConversation);
