using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class BlockingCapabilityCatalogDurabilityBarrier : ICapabilityCatalogDurabilityBarrier
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _callCount;

    public Task Entered => _entered.Task;

    public int CallCount => Volatile.Read(ref _callCount);

    public IOException? Failure { get; init; }

    public void Release() => _release.TrySetResult();

    public void BeforeDirectoryMove(string stagingPath, string destinationPath)
    {
        _ = stagingPath;
        _ = destinationPath;
    }

    public void AfterDirectoryMove(string stagingPath, string destinationPath)
    {
        _ = stagingPath;
        _ = destinationPath;
    }

    public void FlushAfterDirectoryCreate(string directoryPath, SafeFileHandle parentDirectory)
    {
        _ = directoryPath;
        _ = parentDirectory;
    }

    public async ValueTask FlushAfterRenameAsync(string destinationPath, SafeFileHandle parentDirectory)
    {
        _ = destinationPath;
        _ = parentDirectory;
        Interlocked.Increment(ref _callCount);
        _entered.TrySetResult();
        await _release.Task;
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}
