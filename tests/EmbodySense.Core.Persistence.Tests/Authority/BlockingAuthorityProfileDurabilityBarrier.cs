using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Authority;

internal sealed class BlockingAuthorityProfileDurabilityBarrier : ICapabilityCatalogDurabilityBarrier
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _callCount;

    public int TargetCall { get; init; } = 1;

    public IOException? Failure { get; init; }

    public Task Entered => _entered.Task;

    public int CallCount => Volatile.Read(ref _callCount);

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
        var call = Interlocked.Increment(ref _callCount);
        if (call != TargetCall)
        {
            return;
        }

        _entered.TrySetResult();
        await _release.Task;
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}
