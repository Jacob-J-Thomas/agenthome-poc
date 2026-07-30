namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Binds a run to the durable and logical conversation versions captured at admission.
/// </summary>
/// <param name="ConversationId">The conversation identifier.</param>
/// <param name="CapturedVersion">The captured version.</param>
/// <param name="CapturedAtUtc">The captured at utc.</param>
public sealed record LoopRunConversationReference(
    string ConversationId,
    string CapturedVersion,
    DateTimeOffset CapturedAtUtc);
