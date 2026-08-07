using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Web.Tests;

internal sealed class SerializedTestWorkspaceInitializer : IWorkspaceInitializer
{
    private readonly WorkspaceInitializer _inner = WorkspaceInitializer.ForWeb();
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public Task WaitUntilEnteredAsync() => _entered.Task;

    public void Release() => _release.TrySetResult();

    public async Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        _entered.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        await _inner.InitializeAsync(rootPath, cancellationToken);
    }
}
