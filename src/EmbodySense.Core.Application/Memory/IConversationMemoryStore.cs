using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Application.Memory.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Memory.Models;

namespace EmbodySense.Core.Application.Memory;

/// <summary>
/// Persists logical conversation transcripts with versioned, compare-and-append semantics.
/// </summary>
public interface IConversationMemoryStore
{
    /// <summary>
    /// Loads the transcript selected as the current logical conversation.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The current transcript in message order.</returns>
    Task<IReadOnlyList<LlmMessage>> LoadCurrentConversationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the current conversation identity, version, and ordered transcript atomically.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The versioned current-conversation snapshot.</returns>
    Task<ConversationMemorySnapshot> LoadCurrentConversationSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists available logical conversations without loading their full transcripts.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the conversation transcript list items.</returns>
    Task<IReadOnlyList<ConversationTranscriptListItem>> ListConversationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates and selects a fresh empty logical conversation.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task StartFreshConversationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a transcript without changing the current conversation selection.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The selected transcript in message order.</returns>
    Task<IReadOnlyList<LlmMessage>> LoadConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Selects an existing logical conversation as current.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ResumeConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a message to the current conversation.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task AppendMessageAsync(LlmMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically appends <paramref name="message"/> only when the current persisted logical conversation has the expected identity and version
    /// and exactly matches <paramref name="expectedPrefix"/>. Implementations must not perform these comparisons and the append as separable writes.
    /// </summary>
    /// <returns><see langword="true"/> when the compare-and-append committed; otherwise, <see langword="false"/>.</returns>
    Task<bool> TryAppendMessageAsync(string expectedConversationId, string expectedConversationVersion, IReadOnlyList<LlmMessage> expectedPrefix, LlmMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches the current transcript and returns at most the requested number of matches.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="limit">The limit.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The bounded matches with transcript provenance.</returns>
    Task<IReadOnlyList<ConversationMemorySearchResult>> SearchCurrentConversationAsync(string query, int limit = 20, CancellationToken cancellationToken = default);
}
