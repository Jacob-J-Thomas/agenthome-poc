using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Memory.Models;

/// <summary>
/// Represents a conversation memory snapshot.
/// </summary>
/// <param name="ConversationId">The conversation ID.</param>
/// <param name="Version">The version.</param>
/// <param name="Messages">The messages.</param>
public sealed record ConversationMemorySnapshot(
    string ConversationId,
    string Version,
    IReadOnlyList<LlmMessage> Messages);
