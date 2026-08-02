using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.Capabilities;

internal sealed class NativeCapabilityCatalogDurabilityBarrier : ICapabilityCatalogDurabilityBarrier
{
    public static NativeCapabilityCatalogDurabilityBarrier Instance { get; } = new();

    private NativeCapabilityCatalogDurabilityBarrier()
    {
    }

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
        CapabilityCatalogNativeFileSystem.FlushAfterDirectoryCreate(parentDirectory);
    }

    public ValueTask FlushAfterRenameAsync(string destinationPath, SafeFileHandle parentDirectory)
    {
        CapabilityCatalogNativeFileSystem.FlushAfterRename(destinationPath, parentDirectory);
        return ValueTask.CompletedTask;
    }
}
