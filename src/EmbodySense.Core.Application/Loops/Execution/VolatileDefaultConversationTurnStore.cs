using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Loops.Execution;

/// <summary>
/// Retains checkpoint semantics for explicitly non-persistent runner compositions such as focused unit tests.
/// </summary>
internal sealed class VolatileDefaultConversationTurnStore : IDefaultConversationTurnStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DefaultConversationTurnRecord> _records = new(StringComparer.Ordinal);

    public Task<DefaultConversationTurnStoreResult> CreateAsync(DefaultConversationTurnRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_records.TryGetValue(record.TurnId, out var existing))
            {
                var status = existing.LifecycleVersion == record.LifecycleVersion && existing.Checkpoint == record.Checkpoint
                    ? DefaultConversationTurnStoreStatus.Replay
                    : DefaultConversationTurnStoreStatus.Conflict;
                return Task.FromResult(new DefaultConversationTurnStoreResult(status, existing));
            }

            _records.Add(record.TurnId, record);
            return Task.FromResult(new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Created, record));
        }
    }

    public Task<DefaultConversationTurnStoreResult> UpdateAsync(DefaultConversationTurnRecord record, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_records.TryGetValue(record.TurnId, out var existing))
            {
                return Task.FromResult(new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Conflict, null));
            }

            if (existing.LifecycleVersion == record.LifecycleVersion && existing.Checkpoint == record.Checkpoint)
            {
                return Task.FromResult(new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Replay, existing));
            }

            if (existing.LifecycleVersion != expectedLifecycleVersion || record.LifecycleVersion != expectedLifecycleVersion + 1)
            {
                return Task.FromResult(new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Conflict, existing));
            }

            _records[record.TurnId] = record;
            return Task.FromResult(new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Updated, record));
        }
    }

    public Task<DefaultConversationTurnRecord?> LoadAsync(string turnId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(_records.GetValueOrDefault(turnId));
        }
    }

    public Task<IReadOnlyList<DefaultConversationTurnRecord>> ListIncompleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            IReadOnlyList<DefaultConversationTurnRecord> records = _records.Values
                .Where(record => record.Checkpoint < DefaultConversationTurnCheckpoint.Terminal)
                .OrderBy(record => record.Run.StartedAtUtc)
                .ThenBy(record => record.TurnId, StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult(records);
        }
    }

    public Task<IReadOnlyList<DefaultConversationTurnRecord>> ListNeedsReviewAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            IReadOnlyList<DefaultConversationTurnRecord> records = _records.Values
                .Where(record => record.Checkpoint == DefaultConversationTurnCheckpoint.Terminal && record.Run.Status == LoopRunStatus.NeedsReview && record.ReviewResolution is null)
                .OrderBy(record => record.Run.StartedAtUtc)
                .ThenBy(record => record.TurnId, StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult(records);
        }
    }
}
