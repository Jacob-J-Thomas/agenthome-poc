namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Represents a custom loop conversation reference.
/// </summary>
/// <param name="ConversationId">The conversation ID.</param>
/// <param name="CapturedVersion">The captured version.</param>
/// <param name="CapturedAtUtc">The UTC capture time.</param>
public sealed record CustomLoopConversationReference(
    string ConversationId,
    string CapturedVersion,
    DateTimeOffset CapturedAtUtc);
