namespace EmbodySense.Core.Startup.Runtime.Models;

public sealed record AgentRuntimeConversationPublication(
    string OperationId,
    string RunId,
    string LoopId,
    string ConversationId,
    int MessageCount,
    bool AlreadyPublished);
