using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class FailingCapabilityLifecycleDurabilityBarrier : ICapabilityCatalogDurabilityBarrier
{
    internal string? DestinationSuffix { get; init; }
    public void BeforeDirectoryMove(string stagingPath, string destinationPath) { }
    public void AfterDirectoryMove(string stagingPath, string destinationPath) { }
    public void FlushAfterDirectoryCreate(string directoryPath, SafeFileHandle parentDirectory) { }

    public ValueTask FlushAfterRenameAsync(string destinationPath, SafeFileHandle parentDirectory)
    {
        if (DestinationSuffix is not null && destinationPath.EndsWith(DestinationSuffix, StringComparison.Ordinal))
        {
            throw new IOException("Injected lifecycle durability failure.");
        }
        return ValueTask.CompletedTask;
    }
}
