namespace EmbodySense.Core.Application.LocalWorkspace;

/// <summary>Coordinates exact governed workspace filesystem commits with any overlapping authority domain.</summary>
public interface IWorkspaceMutationCommitBoundary
{
    /// <summary>Executes one bounded filesystem commit after acquiring the authority required by every affected path.</summary>
    /// <typeparam name="TResult">The commit result type.</typeparam>
    /// <param name="affectedPaths">The normalized or normalizable filesystem targets affected by the commit, including source and destination paths when applicable.</param>
    /// <param name="commit">The exact filesystem commit callback. Permission, approval, audit, and arbitrary process execution must remain outside this callback.</param>
    /// <param name="cancellationToken">The token used while acquiring authority and executing the commit.</param>
    /// <returns>The commit result.</returns>
    Task<TResult> ExecuteAsync<TResult>(IReadOnlyCollection<string> affectedPaths, Func<CancellationToken, Task<TResult>> commit, CancellationToken cancellationToken = default);
}
