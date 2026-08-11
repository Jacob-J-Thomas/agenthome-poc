using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Requests;

internal sealed class HumanInputFailAfterPrimaryRenameBarrier : ICapabilityCatalogDurabilityBarrier
{
    public void BeforeDirectoryMove(string stagingPath, string destinationPath)
    {
    }

    public void AfterDirectoryMove(string stagingPath, string destinationPath)
    {
    }

    public void FlushAfterDirectoryCreate(string directoryPath, Microsoft.Win32.SafeHandles.SafeFileHandle parentDirectory)
    {
    }

    public ValueTask FlushAfterRenameAsync(
        string destinationPath,
        Microsoft.Win32.SafeHandles.SafeFileHandle parentDirectory)
        => Path.GetFileName(destinationPath) == "lifecycle.json"
            ? ValueTask.FromException(new IOException("Injected failure after the Human Input primary rename."))
            : ValueTask.CompletedTask;
}
