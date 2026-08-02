using EmbodySense.Core.Application.LocalWorkspace;

namespace EmbodySense.Core.Clients.Tests.LocalWorkspace;

internal sealed class ImmediateWorkspaceMutationCommitBoundary : IWorkspaceMutationCommitBoundary
{
    public Task<TResult> ExecuteAsync<TResult>(IReadOnlyCollection<string> affectedPaths, Func<CancellationToken, Task<TResult>> commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(affectedPaths);
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        return commit(cancellationToken);
    }
}
