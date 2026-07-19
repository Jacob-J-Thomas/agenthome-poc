using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Memory;

public sealed record ConversationMemorySnapshot(
    string ConversationId,
    string Version,
    IReadOnlyList<LlmMessage> Messages);
