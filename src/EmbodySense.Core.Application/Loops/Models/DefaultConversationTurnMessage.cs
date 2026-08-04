using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Retains the stable identity and exact canonical content of one turn-owned durable message.
/// </summary>
/// <param name="MessageId">The stable idempotency identity.</param>
/// <param name="Role">The concrete message role.</param>
/// <param name="Content">The exact canonical content.</param>
public sealed record DefaultConversationTurnMessage(string MessageId, LlmMessageRole Role, string Content);
