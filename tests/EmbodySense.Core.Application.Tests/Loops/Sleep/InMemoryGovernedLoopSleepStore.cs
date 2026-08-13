using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sleep;

internal sealed class InMemoryGovernedLoopSleepStore : IGovernedLoopSleepStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, GovernedLoopSleepCheckpoint> _checkpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GovernedLoopWakeEvidence> _wakes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _checkpointClaims = new(StringComparer.Ordinal);

    internal GovernedLoopSleepCheckpointMutationResult? PublishOverride { get; set; }

    internal GovernedLoopWakeEvidenceMutationResult? CreateOverride { get; set; }

    internal GovernedLoopWakeEvidenceMutationResult? AdvanceOverride { get; set; }

    internal GovernedLoopSleepCheckpointReadResult? CheckpointReadOverride { get; set; }

    internal GovernedLoopWakeEvidenceReadResult? WakeReadOverride { get; set; }

    internal Exception? CheckpointReadException { get; set; }

    internal Exception? WakeReadException { get; set; }

    internal bool ThrowAfterPublishCommit { get; set; }

    internal bool ThrowAfterCreateCommit { get; set; }

    internal bool ThrowAfterAdvanceCommit { get; set; }

    internal bool ThrowBeforePublish { get; set; }

    internal bool ThrowBeforeCreate { get; set; }

    internal bool ThrowBeforeAdvance { get; set; }

    internal bool ReturnNullPublish { get; set; }

    internal bool ReturnNullCreate { get; set; }

    internal bool ReturnNullAdvance { get; set; }

    internal bool ReturnNullCheckpointRead { get; set; }

    internal bool ReturnNullWakeRead { get; set; }

    internal Action<GovernedLoopWakeEvidence, CancellationToken>? OnCreate { get; set; }

    internal IReadOnlyList<GovernedLoopWakeDisposition> WrittenDispositions
    {
        get
        {
            lock (_sync)
            {
                return _wakes.Values.Select(value => value.Disposition).ToArray();
            }
        }
    }

    internal int CheckpointCount
    {
        get
        {
            lock (_sync)
            {
                return _checkpoints.Count;
            }
        }
    }

    internal int WakeCount
    {
        get
        {
            lock (_sync)
            {
                return _wakes.Count;
            }
        }
    }

    public Task<GovernedLoopSleepCheckpointMutationResult?> PublishAndReleaseAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        string expectedPostureHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (ThrowBeforePublish)
            {
                throw new InvalidOperationException("simulated publish failure");
            }

            if (ReturnNullPublish)
            {
                return Task.FromResult<GovernedLoopSleepCheckpointMutationResult?>(null);
            }

            if (PublishOverride is not null)
            {
                return Task.FromResult<GovernedLoopSleepCheckpointMutationResult?>(PublishOverride);
            }

            if (_checkpoints.TryGetValue(checkpoint.CheckpointId, out var existing))
            {
                return Task.FromResult<GovernedLoopSleepCheckpointMutationResult?>(
                    new GovernedLoopSleepCheckpointMutationResult(
                        existing.ContentHash == checkpoint.ContentHash
                            ? GovernedLoopSleepCheckpointMutationStatus.Replayed
                            : GovernedLoopSleepCheckpointMutationStatus.Replayed,
                        existing));
            }

            _checkpoints.Add(checkpoint.CheckpointId, checkpoint);
            if (ThrowAfterPublishCommit)
            {
                throw new InvalidOperationException("simulated crash after checkpoint commit");
            }

            return Task.FromResult<GovernedLoopSleepCheckpointMutationResult?>(
                new GovernedLoopSleepCheckpointMutationResult(GovernedLoopSleepCheckpointMutationStatus.Committed, checkpoint));
        }
    }

    public Task<GovernedLoopSleepCheckpointReadResult?> ReadCheckpointAsync(
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (CheckpointReadException is not null)
            {
                throw CheckpointReadException;
            }

            if (CheckpointReadOverride is not null)
            {
                return Task.FromResult<GovernedLoopSleepCheckpointReadResult?>(CheckpointReadOverride);
            }

            if (ReturnNullCheckpointRead)
            {
                return Task.FromResult<GovernedLoopSleepCheckpointReadResult?>(null);
            }

            return Task.FromResult<GovernedLoopSleepCheckpointReadResult?>(
                _checkpoints.TryGetValue(checkpointId, out var checkpoint)
                    ? new GovernedLoopSleepCheckpointReadResult(GovernedLoopSleepStoreReadStatus.Found, checkpoint)
                    : new GovernedLoopSleepCheckpointReadResult(GovernedLoopSleepStoreReadStatus.NotFound));
        }
    }

    public Task<GovernedLoopWakeEvidenceReadResult?> ReadWakeAsync(
        string wakeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (WakeReadException is not null)
            {
                throw WakeReadException;
            }

            if (WakeReadOverride is not null)
            {
                return Task.FromResult<GovernedLoopWakeEvidenceReadResult?>(WakeReadOverride);
            }

            if (ReturnNullWakeRead)
            {
                return Task.FromResult<GovernedLoopWakeEvidenceReadResult?>(null);
            }

            return Task.FromResult<GovernedLoopWakeEvidenceReadResult?>(
                _wakes.TryGetValue(wakeId, out var evidence)
                    ? new GovernedLoopWakeEvidenceReadResult(GovernedLoopSleepStoreReadStatus.Found, evidence)
                    : new GovernedLoopWakeEvidenceReadResult(GovernedLoopSleepStoreReadStatus.NotFound));
        }
    }

    public Task<GovernedLoopWakeEvidenceMutationResult?> CreateWakeAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeEvidence evidence,
        string expectedPostureHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnCreate?.Invoke(evidence, cancellationToken);
        lock (_sync)
        {
            if (ThrowBeforeCreate)
            {
                throw new InvalidOperationException("simulated create failure");
            }

            if (ReturnNullCreate)
            {
                return Task.FromResult<GovernedLoopWakeEvidenceMutationResult?>(null);
            }

            if (CreateOverride is not null)
            {
                return Task.FromResult<GovernedLoopWakeEvidenceMutationResult?>(CreateOverride);
            }

            if (_wakes.TryGetValue(evidence.Identity.WakeId, out var existing))
            {
                return Task.FromResult<GovernedLoopWakeEvidenceMutationResult?>(
                    new GovernedLoopWakeEvidenceMutationResult(GovernedLoopWakeEvidenceMutationStatus.Replayed, existing));
            }

            if (_checkpointClaims.TryGetValue(checkpoint.CheckpointId, out var claimedWakeId))
            {
                return Task.FromResult<GovernedLoopWakeEvidenceMutationResult?>(
                    new GovernedLoopWakeEvidenceMutationResult(
                        GovernedLoopWakeEvidenceMutationStatus.CheckpointClaimed,
                        _wakes[claimedWakeId]));
            }

            _wakes.Add(evidence.Identity.WakeId, evidence);
            _checkpointClaims.Add(checkpoint.CheckpointId, evidence.Identity.WakeId);
            if (ThrowAfterCreateCommit)
            {
                throw new InvalidOperationException("simulated crash after prepared commit");
            }

            return Task.FromResult<GovernedLoopWakeEvidenceMutationResult?>(
                new GovernedLoopWakeEvidenceMutationResult(GovernedLoopWakeEvidenceMutationStatus.Committed, evidence));
        }
    }

    public Task<GovernedLoopWakeEvidenceMutationResult?> AdvanceWakeAsync(
        GovernedLoopWakeEvidence current,
        GovernedLoopWakeEvidence next,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (ThrowBeforeAdvance)
            {
                throw new InvalidOperationException("simulated advance failure");
            }

            if (ReturnNullAdvance)
            {
                return Task.FromResult<GovernedLoopWakeEvidenceMutationResult?>(null);
            }

            if (AdvanceOverride is not null)
            {
                return Task.FromResult<GovernedLoopWakeEvidenceMutationResult?>(AdvanceOverride);
            }

            if (!_wakes.TryGetValue(current.Identity.WakeId, out var stored)
                || stored.ContentHash != current.ContentHash)
            {
                return Task.FromResult<GovernedLoopWakeEvidenceMutationResult?>(
                    new GovernedLoopWakeEvidenceMutationResult(GovernedLoopWakeEvidenceMutationStatus.Conflict));
            }

            _wakes[current.Identity.WakeId] = next;
            if (ThrowAfterAdvanceCommit)
            {
                throw new InvalidOperationException("simulated crash after wake evidence commit");
            }

            return Task.FromResult<GovernedLoopWakeEvidenceMutationResult?>(
                new GovernedLoopWakeEvidenceMutationResult(GovernedLoopWakeEvidenceMutationStatus.Committed, next));
        }
    }

    internal void SeedCheckpoint(GovernedLoopSleepCheckpoint checkpoint)
    {
        lock (_sync)
        {
            _checkpoints[checkpoint.CheckpointId] = checkpoint;
        }
    }

    internal void SeedWake(GovernedLoopWakeEvidence evidence)
    {
        lock (_sync)
        {
            _wakes[evidence.Identity.WakeId] = evidence;
            _checkpointClaims[evidence.Identity.CheckpointId] = evidence.Identity.WakeId;
        }
    }

    internal GovernedLoopWakeEvidence GetWake(string wakeId)
    {
        lock (_sync)
        {
            return _wakes[wakeId];
        }
    }
}
