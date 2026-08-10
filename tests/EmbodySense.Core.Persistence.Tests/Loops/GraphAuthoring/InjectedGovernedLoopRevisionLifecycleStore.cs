using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;

namespace EmbodySense.Core.Persistence.Tests.Loops.GraphAuthoring;

internal sealed class InjectedGovernedLoopRevisionLifecycleStore(
    Func<GovernedLoopRevisionStoreMutation, GovernedLoopRevisionStoreCommitResult> commit)
    : IGovernedLoopRevisionLifecycleStore
{
    public Task<GovernedLoopRevisionGraphReadResult> ReadGraphAsync(
        string graphId,
        CancellationToken cancellationToken = default)
    {
        _ = graphId;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GovernedLoopRevisionGraphReadResult(
            GovernedLoopRevisionStoreReadStatus.NotFound,
            0,
            null));
    }

    public Task<GovernedLoopRevisionStoreReadResult> ReadForMutationAsync(
        string graphId,
        string operationId,
        string requestHash,
        CancellationToken cancellationToken = default)
    {
        _ = graphId;
        _ = operationId;
        _ = requestHash;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GovernedLoopRevisionStoreReadResult(
            GovernedLoopRevisionStoreReadStatus.NotFound,
            0,
            null,
            null));
    }

    public Task<GovernedLoopRevisionStoreCommitResult> CommitAsync(
        GovernedLoopRevisionStoreMutation mutation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(commit(mutation));
    }
}
