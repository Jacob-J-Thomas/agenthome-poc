namespace EmbodySense.Core.Common.Memory.Models;

/// <summary>
/// Represents a conversation transcript list item.
/// </summary>
/// <param name="ConversationId">The conversation ID.</param>
/// <param name="MessageCount">The number of transcript messages.</param>
/// <param name="FirstTimestampUtc">The UTC timestamp of the first message.</param>
/// <param name="LastTimestampUtc">The UTC timestamp of the last message.</param>
/// <param name="FirstPrompt">The first prompt.</param>
/// <param name="IsCurrent">Whether the item identifies the active conversation transcript.</param>
public sealed record ConversationTranscriptListItem(
    string ConversationId,
    int MessageCount,
    DateTimeOffset FirstTimestampUtc,
    DateTimeOffset LastTimestampUtc,
    string? FirstPrompt,
    bool IsCurrent);
