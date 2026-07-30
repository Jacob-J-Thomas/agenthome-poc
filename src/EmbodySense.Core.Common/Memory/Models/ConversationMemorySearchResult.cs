using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Common.Memory.Models;

/// <summary>
/// Represents a conversation memory search result.
/// </summary>
/// <param name="ConversationId">The conversation ID.</param>
/// <param name="Sequence">The sequence.</param>
/// <param name="TimestampUtc">The UTC event time.</param>
/// <param name="Role">The model message role assigned to the content.</param>
/// <param name="Content">The exact content.</param>
public sealed record ConversationMemorySearchResult(
    string ConversationId,
    int Sequence,
    DateTimeOffset TimestampUtc,
    LlmMessageRole Role,
    string Content);
