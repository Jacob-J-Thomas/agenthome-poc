using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring;

internal sealed class GovernedLoopGraphRevisionLifecycleStoreAdapter : IGovernedLoopRevisionLifecycleStore
{
    private readonly IGovernedLoopGraphRevisionStore _store;
    private readonly string _graphId;
    private readonly string _operationId;
    private readonly string _authoringRequestHash;
    private readonly GovernedLoopGraphDefinition? _graphToAppend;
    private readonly string? _graphValidationEvidenceHash;

    internal GovernedLoopGraphRevisionCommitResult? LastCommit { get; private set; }

    internal GovernedLoopGraphRevisionLifecycleStoreAdapter(
        IGovernedLoopGraphRevisionStore store,
        string graphId,
        string operationId,
        string authoringRequestHash,
        GovernedLoopGraphDefinition? graphToAppend,
        string? graphValidationEvidenceHash)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _graphId = graphId;
        _operationId = operationId;
        _authoringRequestHash = authoringRequestHash;
        _graphToAppend = graphToAppend;
        _graphValidationEvidenceHash = graphValidationEvidenceHash;
    }

    public async Task<GovernedLoopRevisionGraphReadResult> ReadGraphAsync(
        string graphId,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(graphId, _graphId, StringComparison.Ordinal))
        {
            return new GovernedLoopRevisionGraphReadResult(
                GovernedLoopRevisionStoreReadStatus.Ambiguous,
                0,
                null);
        }

        var read = await _store.ReadGraphAsync(graphId, cancellationToken).ConfigureAwait(false);
        return read is null
            ? new GovernedLoopRevisionGraphReadResult(GovernedLoopRevisionStoreReadStatus.Ambiguous, 0, null)
            : new GovernedLoopRevisionGraphReadResult(read.Status, read.StoreGeneration, read.Snapshot?.Lifecycle);
    }

    public async Task<GovernedLoopRevisionStoreReadResult> ReadForMutationAsync(
        string graphId,
        string operationId,
        string requestHash,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(graphId, _graphId, StringComparison.Ordinal)
            || !string.Equals(operationId, _operationId, StringComparison.Ordinal)
            || string.IsNullOrEmpty(requestHash))
        {
            return new GovernedLoopRevisionStoreReadResult(
                GovernedLoopRevisionStoreReadStatus.Ambiguous,
                0,
                null,
                null);
        }

        var read = await _store.ReadForMutationAsync(
            graphId,
            operationId,
            requestHash,
            _authoringRequestHash,
            cancellationToken).ConfigureAwait(false);
        return read is null
            ? new GovernedLoopRevisionStoreReadResult(GovernedLoopRevisionStoreReadStatus.Ambiguous, 0, null, null)
            : new GovernedLoopRevisionStoreReadResult(
                read.Status,
                read.StoreGeneration,
                read.Snapshot?.Lifecycle,
                read.ExistingOperation is { State: GovernedLoopGraphRevisionOperationState.Terminal }
                    ? read.ExistingOperation.LifecycleOperation
                    : null);
    }

    public async Task<GovernedLoopRevisionStoreCommitResult> CommitAsync(
        GovernedLoopRevisionStoreMutation mutation,
        CancellationToken cancellationToken = default)
    {
        if (mutation is null
            || !string.Equals(mutation.GraphId, _graphId, StringComparison.Ordinal)
            || !string.Equals(mutation.Operation.OperationId, _operationId, StringComparison.Ordinal))
        {
            return new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.Ambiguous,
                0,
                null,
                null);
        }

        var graph = mutation.ArtifactToAppend is null ? null : _graphToAppend;
        if (mutation.ArtifactToAppend is not null && graph is null)
        {
            return new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.Ambiguous,
                mutation.ExpectedStoreGeneration,
                null,
                null);
        }

        var validationHash = mutation.Operation.PublicationValidationEvidenceHash
            ?? _graphValidationEvidenceHash;
        var commit = await _store.CommitAsync(
            new GovernedLoopGraphRevisionStoreMutation(
                mutation,
                graph,
                _authoringRequestHash,
                validationHash),
            cancellationToken).ConfigureAwait(false);
        LastCommit = commit;
        return commit is null
            ? new GovernedLoopRevisionStoreCommitResult(GovernedLoopRevisionStoreCommitStatus.Ambiguous, 0, null, null)
            : new GovernedLoopRevisionStoreCommitResult(
                commit.Status,
                commit.StoreGeneration,
                commit.Operation is { State: GovernedLoopGraphRevisionOperationState.Terminal }
                    ? commit.Operation.LifecycleOperation
                    : null,
                commit.Snapshot?.Lifecycle);
    }
}
