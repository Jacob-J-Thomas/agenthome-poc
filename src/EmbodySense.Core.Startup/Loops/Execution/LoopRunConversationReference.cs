namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunConversationReference(
    string ConversationId,
    string CapturedVersion,
    DateTimeOffset CapturedAtUtc);
