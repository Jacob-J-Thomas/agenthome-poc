using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Loops.Models.Custom;

/// <summary>
/// Represents a custom loop context input policy.
/// </summary>
/// <param name="IncludeRoleContext">The include role context.</param>
/// <param name="IncludeTriggerPrompt">The include trigger prompt.</param>
/// <param name="IncludeInvokingConversation">The include invoking conversation.</param>
/// <param name="IncludeEarlierRetainedOutputs">The include earlier retained outputs.</param>
/// <param name="IncludePreviousIterationResult">The include previous iteration result.</param>
public sealed record CustomLoopContextInputPolicy(
    [property: JsonRequired] bool IncludeRoleContext,
    [property: JsonRequired] bool IncludeTriggerPrompt,
    [property: JsonRequired] bool IncludeInvokingConversation,
    [property: JsonRequired] bool IncludeEarlierRetainedOutputs,
    [property: JsonRequired] bool IncludePreviousIterationResult);
