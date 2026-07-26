namespace EmbodySense.Web.Models;

public sealed record WebConversationChanged(
    string OperationId,
    string ConversationId,
    int MessageCount);
