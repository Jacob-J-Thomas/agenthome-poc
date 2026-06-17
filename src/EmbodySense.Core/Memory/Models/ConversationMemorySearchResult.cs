using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Core.Memory.Models;

public sealed record ConversationMemorySearchResult(
    string ConversationId,
    int Sequence,
    DateTimeOffset TimestampUtc,
    LlmMessageRole Role,
    string Content);
