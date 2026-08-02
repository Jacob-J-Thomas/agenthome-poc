using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class RecordingCapabilityCatalogDurabilityBarrier : ICapabilityCatalogDurabilityBarrier
{
    private readonly List<string> _events = [];
    private int _directoryCreateCount;

    public int? FailDirectoryCreateAt { get; init; }

    public IReadOnlyList<string> Events => _events.ToArray();

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
        _ = parentDirectory;
        _events.Add("directory:" + directoryPath);
        _directoryCreateCount++;
        if (_directoryCreateCount == FailDirectoryCreateAt)
        {
            throw new IOException("Injected directory-entry durability failure.");
        }
    }

    public ValueTask FlushAfterRenameAsync(string destinationPath, SafeFileHandle parentDirectory)
    {
        _ = parentDirectory;
        _events.Add("rename:" + destinationPath);
        return ValueTask.CompletedTask;
    }
}
