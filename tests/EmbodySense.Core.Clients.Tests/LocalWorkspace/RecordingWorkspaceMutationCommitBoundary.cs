using EmbodySense.Core.Application.LocalWorkspace;

namespace EmbodySense.Core.Clients.Tests.LocalWorkspace;

internal sealed class RecordingWorkspaceMutationCommitBoundary : IWorkspaceMutationCommitBoundary
{
    private readonly List<IReadOnlyList<string>> _affectedPaths = [];

    internal IReadOnlyList<IReadOnlyList<string>> AffectedPaths => _affectedPaths;

    public async Task<TResult> ExecuteAsync<TResult>(IReadOnlyCollection<string> affectedPaths, Func<CancellationToken, Task<TResult>> commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(affectedPaths);
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        _affectedPaths.Add(affectedPaths.ToArray());
        return await commit(cancellationToken);
    }
}
