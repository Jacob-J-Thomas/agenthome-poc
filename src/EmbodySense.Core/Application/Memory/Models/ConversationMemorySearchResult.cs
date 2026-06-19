using EmbodySense.Core.Application.Inference.Models;

namespace EmbodySense.Core.Application.Memory.Models;

public sealed record ConversationMemorySearchResult(
    string ConversationId,
    int Sequence,
    DateTimeOffset TimestampUtc,
    LlmMessageRole Role,
    string Content);
