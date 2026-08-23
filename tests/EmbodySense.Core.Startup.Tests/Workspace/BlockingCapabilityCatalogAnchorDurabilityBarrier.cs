using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Startup.Tests.Workspace;

internal sealed class BlockingCapabilityCatalogAnchorDurabilityBarrier : ICapabilityCatalogDurabilityBarrier
{
    private readonly TaskCompletionSource _anchorWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task AnchorWriteEntered => _anchorWriteEntered.Task;

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
        _ = parentDirectory;
        if (!destinationPath.EndsWith(".json", StringComparison.Ordinal))
        {
            return;
        }

        _anchorWriteEntered.TrySetResult();
        await _release.Task;
    }
}
