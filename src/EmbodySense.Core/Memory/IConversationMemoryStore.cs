using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Memory.Models;

namespace EmbodySense.Core.Memory;

public interface IConversationMemoryStore
{
    Task<IReadOnlyList<LlmMessage>> LoadCurrentConversationAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationTranscriptListItem>> ListConversationsAsync(CancellationToken cancellationToken = default);

    Task StartFreshConversationAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LlmMessage>> LoadConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    Task ResumeConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    Task AppendMessageAsync(LlmMessage message, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMemorySearchResult>> SearchCurrentConversationAsync(string query, int limit = 20, CancellationToken cancellationToken = default);
}
