using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Responses;

internal sealed class InMemoryHumanInputResponseLifecycleStore(
    HumanInputRequestLifecycleStoreSnapshot? lifecycle) : IHumanInputResponseLifecycleStore
{
    private readonly Dictionary<string, HumanInputResponseLifecycleStoredOperation> _operationsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HumanInputResponseArtifact>> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HumanInputResponseOperationEvidence>> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HumanInputResponseSelection?> _selections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private HumanInputRequestLifecycleStoreSnapshot? _lifecycle = lifecycle;
    private long _generation = lifecycle?.Operations.Count ?? 0;

    internal List<HumanInputResponseLifecycleStoreMutation> Commits { get; } = [];

    internal List<(string RequestId, string OperationId, string CommandHash)> MutationReads { get; } = [];

    internal Func<string, string, string, CancellationToken, Task<HumanInputResponseLifecycleStoreReadResult>>? ReadForMutationOverride { get; set; }

    internal Func<HumanInputResponseLifecycleStoreMutation, CancellationToken, Task<HumanInputResponseLifecycleStoreCommitResult>>? CommitOverride { get; set; }

    internal int ConflictsRemaining { get; set; }

    internal bool ThrowAfterCommit { get; set; }

    internal Action? AfterDurableCommit { get; set; }

    internal Barrier? ReadyReadBarrier { get; set; }

    internal bool LastCommitTokenCanBeCanceled { get; private set; }

    internal HumanInputResponseLifecycleStoreSnapshot? CurrentSnapshot
        => _lifecycle is null ? null : Snapshot(_lifecycle.Head.CurrentRequest);

    public Task<HumanInputResponseLifecycleStoreReadResult> ReadAsync(
        HumanInputRequestReference request,
        CancellationToken cancellationToken = default)
    {
        var snapshot = Snapshot(request);
        return Task.FromResult(
            new HumanInputResponseLifecycleStoreReadResult(
                snapshot is null ? HumanInputResponseLifecycleStoreReadStatus.NotFound : HumanInputResponseLifecycleStoreReadStatus.Ready,
                _generation,
                snapshot,
                null));
    }

    public Task<HumanInputResponseLifecycleStoreReadResult> ReadForMutationAsync(
        string requestId,
        string operationId,
        string commandHash,
        CancellationToken cancellationToken = default)
    {
        MutationReads.Add((requestId, operationId, commandHash));
        if (ReadForMutationOverride is not null)
        {
            return ReadForMutationOverride(requestId, operationId, commandHash, cancellationToken);
        }
        if (_operationsById.TryGetValue(operationId, out var existing))
        {
            var exact = string.Equals(existing.RequestId, requestId, StringComparison.Ordinal)
                && string.Equals(existing.Evidence.CommandHash, commandHash, StringComparison.Ordinal);
            var snapshot = exact
                ? existing.Evidence.FailureCode == HumanInputResponseOperationFailureCode.RequestNotFound
                    ? null
                    : Snapshot(existing.Evidence.Request) ?? CurrentSnapshot
                : CurrentSnapshot;
            return Task.FromResult(
                new HumanInputResponseLifecycleStoreReadResult(
                    exact
                        ? snapshot is null
                            ? HumanInputResponseLifecycleStoreReadStatus.NotFound
                            : HumanInputResponseLifecycleStoreReadStatus.Ready
                        : HumanInputResponseLifecycleStoreReadStatus.OperationConflict,
                    _generation,
                    snapshot,
                    existing));
        }
        var current = CurrentSnapshot;
        ReadyReadBarrier?.SignalAndWait(cancellationToken);
        return Task.FromResult(
            new HumanInputResponseLifecycleStoreReadResult(
                current is null ? HumanInputResponseLifecycleStoreReadStatus.NotFound : HumanInputResponseLifecycleStoreReadStatus.Ready,
                _generation,
                current,
                null));
    }

    public async Task<HumanInputResponseLifecycleStoreCommitResult> CommitAsync(
        HumanInputResponseLifecycleStoreMutation mutation,
        CancellationToken cancellationToken = default)
    {
        LastCommitTokenCanBeCanceled = cancellationToken.CanBeCanceled;
        if (CommitOverride is not null)
        {
            Commits.Add(mutation);
            return await CommitOverride(mutation, cancellationToken);
        }
        await _commitGate.WaitAsync(CancellationToken.None);
        try
        {
            Commits.Add(mutation);
            return CommitCore(mutation);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    private HumanInputResponseLifecycleStoreCommitResult CommitCore(HumanInputResponseLifecycleStoreMutation mutation)
    {
        if (ConflictsRemaining > 0)
        {
            ConflictsRemaining--;
            _generation++;
            return new HumanInputResponseLifecycleStoreCommitResult(
                HumanInputResponseLifecycleStoreCommitStatus.StoreConflict,
                _generation,
                null,
                CurrentSnapshot);
        }
        if (_operationsById.TryGetValue(mutation.Operation.OperationId, out var retained))
        {
            return new HumanInputResponseLifecycleStoreCommitResult(
                Equals(retained.Evidence, mutation.Operation)
                    ? HumanInputResponseLifecycleStoreCommitStatus.Replayed
                    : HumanInputResponseLifecycleStoreCommitStatus.OperationConflict,
                _generation,
                retained,
                Snapshot(retained.Evidence.Request) ?? CurrentSnapshot);
        }
        if (mutation.ExpectedStoreGeneration != _generation)
        {
            return new HumanInputResponseLifecycleStoreCommitResult(
                HumanInputResponseLifecycleStoreCommitStatus.StoreConflict,
                _generation,
                null,
                CurrentSnapshot);
        }

        _generation++;
        var stored = new HumanInputResponseLifecycleStoredOperation(mutation.Operation.Request.RequestId, mutation.Operation);
        _operationsById.Add(mutation.Operation.OperationId, stored);
        var requestIsRetained = RetainedRequest(mutation.Operation.Request) is not null;
        if (requestIsRetained)
        {
            var key = Key(mutation.Operation.Request);
            Operations(key).Add(mutation.Operation);
            if (mutation.ResponseToAppend is not null)
            {
                Responses(key).Add(mutation.ResponseToAppend);
            }
            if (mutation.SelectionToAppend is not null)
            {
                _selections[key] = mutation.SelectionToAppend;
            }
        }
        if (mutation.RequestHeadToWrite is not null && _lifecycle is not null)
        {
            _lifecycle = new HumanInputRequestLifecycleStoreSnapshot(
                mutation.RequestHeadToWrite,
                _lifecycle.RequestVersions,
                _lifecycle.Operations,
                mutation.Operation);
        }
        var snapshot = mutation.Operation.FailureCode == HumanInputResponseOperationFailureCode.RequestNotFound
            ? null
            : requestIsRetained
                ? Snapshot(mutation.Operation.Request)
                : CurrentSnapshot;
        var result = new HumanInputResponseLifecycleStoreCommitResult(
            HumanInputResponseLifecycleStoreCommitStatus.Committed,
            _generation,
            stored,
            snapshot);
        AfterDurableCommit?.Invoke();
        if (ThrowAfterCommit)
        {
            ThrowAfterCommit = false;
            throw new InvalidOperationException("Simulated crash after durable commit.");
        }
        return result;
    }

    internal void ReplaceLifecycle(HumanInputRequestLifecycleStoreSnapshot lifecycle)
    {
        _lifecycle = lifecycle;
        _generation++;
    }

    internal void ReplaceCurrentSnapshot(HumanInputResponseLifecycleStoreSnapshot snapshot)
    {
        _lifecycle = snapshot.Request;
        var key = Key(snapshot.ResponseRequest);
        _responses[key] = snapshot.Responses.ToList();
        _operations[key] = snapshot.Operations.ToList();
        _selections[key] = snapshot.Selection;
    }

    internal void SeedCurrentSnapshot(HumanInputResponseLifecycleStoreSnapshot snapshot)
    {
        ReplaceCurrentSnapshot(snapshot);
        _operationsById.Clear();
        foreach (var operation in snapshot.Operations)
        {
            if (!_operationsById.TryAdd(operation.OperationId, new HumanInputResponseLifecycleStoredOperation(operation.Request.RequestId, operation)))
            {
                throw new InvalidOperationException("The seeded response history contains a duplicate operation identity.");
            }
        }
        _generation = checked(snapshot.Request.Operations.Count + snapshot.Operations.Count);
    }

    private HumanInputResponseLifecycleStoreSnapshot? Snapshot(HumanInputRequestReference reference)
    {
        if (_lifecycle is null || RetainedRequest(reference) is null)
        {
            return null;
        }
        var key = Key(reference);
        return new HumanInputResponseLifecycleStoreSnapshot(
            _lifecycle,
            reference,
            Responses(key),
            Operations(key),
            _selections.GetValueOrDefault(key));
    }

    private HumanInputRequest? RetainedRequest(HumanInputRequestReference reference)
        => _lifecycle?.RequestVersions.SingleOrDefault(reference.Matches);

    private List<HumanInputResponseArtifact> Responses(string key)
    {
        if (!_responses.TryGetValue(key, out var responses))
        {
            responses = [];
            _responses.Add(key, responses);
        }
        return responses;
    }

    private List<HumanInputResponseOperationEvidence> Operations(string key)
    {
        if (!_operations.TryGetValue(key, out var operations))
        {
            operations = [];
            _operations.Add(key, operations);
        }
        return operations;
    }

    private static string Key(HumanInputRequestReference request)
        => $"{request.RequestId}\n{request.RequestVersionId}\n{request.RequestHash}";
}
