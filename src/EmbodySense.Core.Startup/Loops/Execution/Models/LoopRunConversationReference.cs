namespace EmbodySense.Core.Startup.Loops.Execution.Models;

public sealed record LoopRunConversationReference(
    string ConversationId,
    string CapturedVersion,
    DateTimeOffset CapturedAtUtc);
