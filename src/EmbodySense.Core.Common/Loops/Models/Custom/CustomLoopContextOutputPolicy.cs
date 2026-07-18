using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Loops.Models.Custom;

public sealed record CustomLoopContextOutputPolicy(
    [property: JsonRequired] bool RetainForLoopReasoning,
    [property: JsonRequired] bool PublishToInvokingConversation);
