using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Memory.Models;

namespace EmbodySense.Core.Application.Memory;

public interface IConversationMemoryStore
{
    Task<IReadOnlyList<LlmMessage>> LoadCurrentConversationAsync(CancellationToken cancellationToken = default);

    Task<ConversationMemorySnapshot> LoadCurrentConversationSnapshotAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationTranscriptListItem>> ListConversationsAsync(CancellationToken cancellationToken = default);

    Task StartFreshConversationAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LlmMessage>> LoadConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    Task ResumeConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    Task AppendMessageAsync(LlmMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically appends <paramref name="message"/> only when the current persisted logical conversation has the expected identity and version
    /// and exactly matches <paramref name="expectedPrefix"/>. Implementations must not perform these comparisons and the append as separable writes.
    /// </summary>
    Task<bool> TryAppendMessageAsync(string expectedConversationId, string expectedConversationVersion, IReadOnlyList<LlmMessage> expectedPrefix, LlmMessage message, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMemorySearchResult>> SearchCurrentConversationAsync(string query, int limit = 20, CancellationToken cancellationToken = default);
}
