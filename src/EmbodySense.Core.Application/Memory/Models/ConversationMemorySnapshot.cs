using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Memory.Models;

public sealed record ConversationMemorySnapshot(
    string ConversationId,
    string Version,
    IReadOnlyList<LlmMessage> Messages);
