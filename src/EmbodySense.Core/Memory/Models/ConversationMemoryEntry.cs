namespace EmbodySense.Core.Memory.Models;

public sealed record ConversationMemoryEntry(
    int SchemaVersion,
    string ConversationId,
    int Sequence,
    DateTimeOffset TimestampUtc,
    string Role,
    string Content);
