using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Loops.Models.Custom;

public sealed record CustomLoopTriggerPolicy(
    CustomLoopTriggerPromptSource PromptSource,
    string PresetPrompt,
    [property: JsonRequired] bool IncludeInvokingConversation);
