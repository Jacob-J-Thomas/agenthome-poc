using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

internal sealed class InMemoryHumanInputRequestLifecycleStore : IHumanInputRequestLifecycleStore
{
    private readonly Dictionary<string, HumanInputRequestLifecycleStoredOperation> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HumanInputRequestLifecycleStoreSnapshot> _snapshots = new(StringComparer.Ordinal);
    private long _generation;

    internal List<(string RequestId, CancellationToken CancellationToken)> Reads { get; } = [];

    internal List<(string RequestId, string OperationId, string RequestHash, string? RelatedRequestId, CancellationToken CancellationToken)> MutationReads { get; } = [];

    internal List<(HumanInputRequestLifecycleStoreMutation Mutation, CancellationToken CancellationToken)> Commits { get; } = [];

    internal Func<string, CancellationToken, Task<HumanInputRequestLifecycleStoreReadResult>>? ReadOverride { get; set; }

    internal Func<string, string, string, string?, CancellationToken, Task<HumanInputRequestLifecycleStoreReadResult>>? ReadForMutationOverride { get; set; }

    internal Func<HumanInputRequestLifecycleStoreMutation, CancellationToken, Task<HumanInputRequestLifecycleStoreCommitResult>>? CommitOverride { get; set; }

    public Task<HumanInputRequestLifecycleStoreReadResult> ReadAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        Reads.Add((requestId, cancellationToken));
        if (ReadOverride is not null)
        {
            return ReadOverride(requestId, cancellationToken);
        }

        _snapshots.TryGetValue(requestId, out var snapshot);
        return Task.FromResult(new HumanInputRequestLifecycleStoreReadResult(
            snapshot is null ? HumanInputRequestLifecycleStoreReadStatus.NotFound : HumanInputRequestLifecycleStoreReadStatus.Ready,
            _generation,
            snapshot,
            null,
            null));
    }

    public Task<HumanInputRequestLifecycleStoreReadResult> ReadForMutationAsync(
        string requestId,
        string operationId,
        string requestHash,
        string? relatedRequestId = null,
        CancellationToken cancellationToken = default)
    {
        MutationReads.Add((requestId, operationId, requestHash, relatedRequestId, cancellationToken));
        if (ReadForMutationOverride is not null)
        {
            return ReadForMutationOverride(requestId, operationId, requestHash, relatedRequestId, cancellationToken);
        }

        _snapshots.TryGetValue(requestId, out var primary);
        _operations.TryGetValue(operationId, out var existing);
        var exactOperation = existing is not null
            && string.Equals(existing.RequestId, requestId, StringComparison.Ordinal)
            && string.Equals(existing.Evidence.RequestHash, requestHash, StringComparison.Ordinal);
        if (existing is not null && !exactOperation)
        {
            return Task.FromResult(new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.OperationConflict,
                _generation,
                primary,
                null,
                existing));
        }

        HumanInputRequestLifecycleStoreSnapshot? related = null;
        if (relatedRequestId is not null)
        {
            _snapshots.TryGetValue(relatedRequestId, out related);
        }

        return Task.FromResult(new HumanInputRequestLifecycleStoreReadResult(
            primary is null ? HumanInputRequestLifecycleStoreReadStatus.NotFound : HumanInputRequestLifecycleStoreReadStatus.Ready,
            _generation,
            primary,
            related,
            existing));
    }

    public Task<HumanInputRequestLifecycleStoreCommitResult> CommitAsync(
        HumanInputRequestLifecycleStoreMutation mutation,
        CancellationToken cancellationToken = default)
    {
        Commits.Add((mutation, cancellationToken));
        return CommitOverride?.Invoke(mutation, cancellationToken) ?? Task.FromResult(CommitDurably(mutation));
    }

    internal HumanInputRequestLifecycleStoreSnapshot? Snapshot(string requestId)
        => _snapshots.GetValueOrDefault(requestId);

    internal void ReplaceSnapshot(string requestId, HumanInputRequestLifecycleStoreSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshots[requestId] = snapshot;
        _generation++;
    }

    internal HumanInputRequestLifecycleStoreCommitResult CommitDurably(HumanInputRequestLifecycleStoreMutation mutation)
    {
        if (_operations.TryGetValue(mutation.Operation.OperationId, out var existing))
        {
            return new HumanInputRequestLifecycleStoreCommitResult(
                Equals(existing.Evidence, mutation.Operation)
                    ? HumanInputRequestLifecycleStoreCommitStatus.Replayed
                    : HumanInputRequestLifecycleStoreCommitStatus.OperationConflict,
                _generation,
                existing,
                _snapshots.GetValueOrDefault(existing.RequestId),
                existing.Evidence.RelatedRequestId is null
                    ? null
                    : _snapshots.GetValueOrDefault(existing.Evidence.RelatedRequestId));
        }

        if (mutation.ExpectedStoreGeneration != _generation)
        {
            return new HumanInputRequestLifecycleStoreCommitResult(
                HumanInputRequestLifecycleStoreCommitStatus.StoreConflict,
                _generation,
                null,
                null,
                null);
        }

        var stored = new HumanInputRequestLifecycleStoredOperation(mutation.Operation.TargetRequestId, mutation.Operation);
        _operations.Add(mutation.Operation.OperationId, stored);
        var primary = ApplyPrimary(mutation);
        var related = ApplyRelated(mutation);
        _generation++;
        return new HumanInputRequestLifecycleStoreCommitResult(
            HumanInputRequestLifecycleStoreCommitStatus.Committed,
            _generation,
            stored,
            primary,
            related);
    }

    private HumanInputRequestLifecycleStoreSnapshot? ApplyPrimary(HumanInputRequestLifecycleStoreMutation mutation)
    {
        _snapshots.TryGetValue(mutation.Operation.TargetRequestId, out var existing);
        if (existing is null && mutation.PrimaryHeadToWrite is null)
        {
            return null;
        }

        var requests = existing?.RequestVersions.ToList() ?? [];
        if (mutation.RequestToAppend is { } request
            && string.Equals(request.RequestId, mutation.Operation.TargetRequestId, StringComparison.Ordinal))
        {
            requests.Add(request);
        }

        var operations = existing?.Operations.ToList() ?? [];
        operations.Add(mutation.Operation);
        var snapshot = new HumanInputRequestLifecycleStoreSnapshot(
            mutation.PrimaryHeadToWrite ?? existing!.Head,
            requests,
            operations);
        _snapshots[mutation.Operation.TargetRequestId] = snapshot;
        return snapshot;
    }

    private HumanInputRequestLifecycleStoreSnapshot? ApplyRelated(HumanInputRequestLifecycleStoreMutation mutation)
    {
        if (mutation.Operation.RelatedRequestId is not { } relatedRequestId)
        {
            return null;
        }

        _snapshots.TryGetValue(relatedRequestId, out var existing);
        if (mutation.SecondaryHeadToWrite is not { } head || mutation.RequestToAppend is not { } request)
        {
            if (existing is null)
            {
                return null;
            }

            var retained = new HumanInputRequestLifecycleStoreSnapshot(
                existing.Head,
                existing.RequestVersions,
                existing.Operations.Append(mutation.Operation).ToArray());
            _snapshots[relatedRequestId] = retained;
            return retained;
        }

        var snapshot = new HumanInputRequestLifecycleStoreSnapshot(head, [request], [mutation.Operation]);
        _snapshots[relatedRequestId] = snapshot;
        return snapshot;
    }
}
