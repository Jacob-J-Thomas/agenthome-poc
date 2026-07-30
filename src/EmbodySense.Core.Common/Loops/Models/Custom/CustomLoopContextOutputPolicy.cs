using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Loops.Models.Custom;

/// <summary>
/// Represents a custom loop context output policy.
/// </summary>
/// <param name="RetainForLoopReasoning">The retain for loop reasoning.</param>
/// <param name="PublishToInvokingConversation">The publish to invoking conversation.</param>
public sealed record CustomLoopContextOutputPolicy(
    [property: JsonRequired] bool RetainForLoopReasoning,
    [property: JsonRequired] bool PublishToInvokingConversation);
