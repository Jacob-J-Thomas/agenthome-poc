namespace EmbodySense.Core.Startup.Loops;

public sealed record LoopTriggerPolicy(
    LoopTriggerPromptSource PromptSource,
    string PresetPrompt,
    bool IncludeInvokingConversation);
