namespace EmbodySense.Web.Models;

/// <summary>
/// Identifies a committed default-conversation change broadcast to authenticated browser clients.
/// </summary>
/// <param name="OperationId">The publication operation that produced this notification.</param>
/// <param name="ConversationId">The durable logical conversation identity.</param>
/// <param name="MessageCount">The complete durable transcript message count after publication.</param>
public sealed record WebConversationChanged(
    string OperationId,
    string ConversationId,
    int MessageCount);
