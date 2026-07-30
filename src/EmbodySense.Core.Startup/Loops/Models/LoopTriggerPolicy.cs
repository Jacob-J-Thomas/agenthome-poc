namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Defines how a manual invocation resolves its trigger prompt and conversation admission.
/// </summary>
/// <param name="PromptSource">Whether the prompt comes from invocation input, a preset, or no prompt.</param>
/// <param name="PresetPrompt">The definition-owned prompt used only in preset mode.</param>
/// <param name="IncludeInvokingConversation">Whether bounded logical conversation content is admitted to the run snapshot.</param>
public sealed record LoopTriggerPolicy(
    LoopTriggerPromptSource PromptSource,
    string PresetPrompt,
    bool IncludeInvokingConversation);
