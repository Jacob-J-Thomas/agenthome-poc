using EmbodySense.Core.Application.LocalWorkspace;

namespace EmbodySense.IntegrationTests.Core.Capabilities;

internal sealed class BlockingWorkspaceMutationCommitBoundary(IWorkspaceMutationCommitBoundary inner) : IWorkspaceMutationCommitBoundary
{
    private readonly TaskCompletionSource _commitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _commitRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task CommitEntered => _commitEntered.Task;

    internal void ReleaseCommit() => _commitRelease.TrySetResult();

    public Task<TResult> ExecuteAsync<TResult>(IReadOnlyCollection<string> affectedPaths, Func<CancellationToken, Task<TResult>> commit, CancellationToken cancellationToken = default)
    {
        return inner.ExecuteAsync(affectedPaths, async commitCancellationToken =>
        {
            _commitEntered.TrySetResult();
            await _commitRelease.Task.WaitAsync(commitCancellationToken);
            return await commit(commitCancellationToken);
        }, cancellationToken);
    }
}
