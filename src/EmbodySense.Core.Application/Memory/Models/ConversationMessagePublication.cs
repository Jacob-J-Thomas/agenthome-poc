using EmbodySense.Core.Common.Inference;

namespace EmbodySense.Core.Application.Memory.Models;

/// <summary>
/// Identifies one exact transcript message publication independently from its role and content.
/// </summary>
/// <param name="MessageId">The stable message identity.</param>
/// <param name="PublicationId">The stable publication identity.</param>
/// <param name="Message">The exact role and content.</param>
public sealed record ConversationMessagePublication(string MessageId, string PublicationId, LlmMessage Message);
