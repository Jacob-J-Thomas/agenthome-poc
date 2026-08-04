namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the explicit human disposition of an outcome-unknown default-conversation turn.
/// </summary>
public enum DefaultConversationTurnReviewDisposition
{
    /// <summary>No concrete disposition was selected.</summary>
    Unknown = 0,
    /// <summary>The ambiguous attempt was inspected and abandoned without publishing or redispatching it.</summary>
    Abandoned
}
