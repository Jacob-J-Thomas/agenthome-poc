namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>
/// Identifies one custom-loop publication that durably joined the active runtime conversation.
/// </summary>
/// <param name="OperationId">The idempotent publication operation identity.</param>
/// <param name="RunId">The publishing custom-loop run.</param>
/// <param name="LoopId">The publishing loop definition.</param>
/// <param name="ConversationId">The durable invoking conversation identity.</param>
/// <param name="MessageCount">The verified durable message count after publication.</param>
/// <param name="AlreadyPublished">Whether reconciliation found the exact output already present instead of appending it now.</param>
public sealed record AgentRuntimeConversationPublication(
    string OperationId,
    string RunId,
    string LoopId,
    string ConversationId,
    int MessageCount,
    bool AlreadyPublished);
