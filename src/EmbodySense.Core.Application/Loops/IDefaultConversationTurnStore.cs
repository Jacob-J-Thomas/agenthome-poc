using EmbodySense.Core.Application.Loops.Models;

namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Persists canonical default-conversation intent, outbox, checkpoint, and transition evidence.
/// </summary>
public interface IDefaultConversationTurnStore
{
    /// <summary>Creates one admitted record or replays the exact creation.</summary>
    Task<DefaultConversationTurnStoreResult> CreateAsync(DefaultConversationTurnRecord record, CancellationToken cancellationToken = default);

    /// <summary>Advances one record only from the exact expected lifecycle version.</summary>
    Task<DefaultConversationTurnStoreResult> UpdateAsync(DefaultConversationTurnRecord record, int expectedLifecycleVersion, CancellationToken cancellationToken = default);

    /// <summary>Loads one record by stable turn identity.</summary>
    Task<DefaultConversationTurnRecord?> LoadAsync(string turnId, CancellationToken cancellationToken = default);

    /// <summary>Lists every nonterminal turn in deterministic admission order.</summary>
    Task<IReadOnlyList<DefaultConversationTurnRecord>> ListIncompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists terminal review-required turns in deterministic admission order.</summary>
    Task<IReadOnlyList<DefaultConversationTurnRecord>> ListNeedsReviewAsync(CancellationToken cancellationToken = default);
}
