using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Loops.Models.Custom;

/// <summary>
/// Represents a custom loop trigger policy.
/// </summary>
/// <param name="PromptSource">The prompt source.</param>
/// <param name="PresetPrompt">The preset prompt.</param>
/// <param name="IncludeInvokingConversation">The include invoking conversation.</param>
public sealed record CustomLoopTriggerPolicy(
    CustomLoopTriggerPromptSource PromptSource,
    string PresetPrompt,
    [property: JsonRequired] bool IncludeInvokingConversation);
