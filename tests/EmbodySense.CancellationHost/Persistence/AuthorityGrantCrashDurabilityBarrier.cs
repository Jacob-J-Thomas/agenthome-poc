using EmbodySense.Core.Persistence.Capabilities;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class AuthorityGrantCrashDurabilityBarrier(string markerPath, int targetFlushCount) : ICapabilityCatalogDurabilityBarrier
{
    private int _flushCount;

    public void BeforeDirectoryMove(string stagingPath, string destinationPath)
    {
    }

    public void AfterDirectoryMove(string stagingPath, string destinationPath)
    {
    }

    public void FlushAfterDirectoryCreate(string directoryPath, SafeFileHandle parentDirectory)
    {
    }

    public async ValueTask FlushAfterRenameAsync(string destinationPath, SafeFileHandle parentDirectory)
    {
        if (Interlocked.Increment(ref _flushCount) != targetFlushCount)
        {
            return;
        }

        await File.WriteAllTextAsync(markerPath, destinationPath);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}
