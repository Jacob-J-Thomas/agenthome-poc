using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Common.Memory.Models;

public sealed record ConversationMemorySearchResult(
    string ConversationId,
    int Sequence,
    DateTimeOffset TimestampUtc,
    LlmMessageRole Role,
    string Content);
