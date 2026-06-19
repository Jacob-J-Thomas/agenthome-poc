namespace EmbodySense.Core.Application.Memory.Models;

public sealed record ConversationTranscriptListItem(
    string ConversationId,
    int MessageCount,
    DateTimeOffset FirstTimestampUtc,
    DateTimeOffset LastTimestampUtc,
    string? FirstPrompt,
    bool IsCurrent);
