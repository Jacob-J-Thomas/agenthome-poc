namespace EmbodySense.Core.Startup.Loops.Models;

public sealed record LoopContextOutputPolicy(
    bool RetainForLoopReasoning,
    bool PublishToInvokingConversation);
