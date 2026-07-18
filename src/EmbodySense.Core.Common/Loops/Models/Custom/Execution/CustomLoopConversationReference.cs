namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public sealed record CustomLoopConversationReference(
    string ConversationId,
    string CapturedVersion,
    DateTimeOffset CapturedAtUtc);
