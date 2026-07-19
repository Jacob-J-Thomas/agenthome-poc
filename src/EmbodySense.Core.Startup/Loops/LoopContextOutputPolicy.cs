namespace EmbodySense.Core.Startup.Loops;

public sealed record LoopContextOutputPolicy(
    bool RetainForLoopReasoning,
    bool PublishToInvokingConversation);
