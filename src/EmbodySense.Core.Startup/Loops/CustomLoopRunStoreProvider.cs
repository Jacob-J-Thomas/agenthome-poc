using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Core.Startup.Loops;

/// <summary>
/// Owns one canonical custom-loop run store for a single workspace-host lifetime.
/// </summary>
/// <remarks>
/// Authoring facades and configured runtimes borrow the same store. The provider is inference-independent
/// and is the only disposal owner when it is supplied to runtime composition; callers must dispose it only
/// after every borrower has completed or been disposed.
/// </remarks>
public sealed class CustomLoopRunStoreProvider : IAsyncDisposable
{
    private readonly WorkspacePaths _paths;
    private readonly CustomLoopRunStore _runStore;
    private int _disposed;

    /// <summary>
    /// Creates an inference-independent owner for one workspace's canonical custom-loop run store.
    /// </summary>
    /// <param name="workingDirectory">The workspace root whose canonical run store is owned.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="workingDirectory"/> is blank.</exception>
    public CustomLoopRunStoreProvider(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        _paths = new WorkspacePaths(workingDirectory);
        _runStore = new CustomLoopRunStore(_paths);
    }

    /// <summary>
    /// Creates an authoring facade that borrows this provider's canonical run store.
    /// </summary>
    /// <param name="actor">The nonblank actor attributed to authoring audit events.</param>
    /// <returns>An authoring facade that remains valid until this provider is disposed.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the provider has already been disposed.</exception>
    public LoopAuthoringFacade CreateLoopAuthoringFacade(string actor = WorkspaceActors.Web)
    {
        ThrowIfDisposed();
        return new LoopAuthoringFacade(_paths.RootPath, _runStore, actor);
    }

    /// <summary>
    /// Disposes the owned store once after every authoring and runtime borrower has completed.
    /// </summary>
    /// <returns>A completed value task after the store's monitoring resources are released.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _runStore.Dispose();
        return ValueTask.CompletedTask;
    }

    internal CustomLoopRunStore Borrow(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ThrowIfDisposed();

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(_paths.RootPath, paths.RootPath, comparison))
        {
            throw new ArgumentException("The canonical custom-loop run store belongs to a different workspace.", nameof(paths));
        }

        return _runStore;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
    }
}
