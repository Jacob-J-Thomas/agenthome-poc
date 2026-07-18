using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Loops.Models.Custom;

public sealed record CustomLoopContextInputPolicy(
    [property: JsonRequired] bool IncludeRoleContext,
    [property: JsonRequired] bool IncludeTriggerPrompt,
    [property: JsonRequired] bool IncludeInvokingConversation,
    [property: JsonRequired] bool IncludeEarlierRetainedOutputs,
    [property: JsonRequired] bool IncludePreviousIterationResult);
