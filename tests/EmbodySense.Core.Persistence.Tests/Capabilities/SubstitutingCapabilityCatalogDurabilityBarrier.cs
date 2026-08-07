using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class SubstitutingCapabilityCatalogDurabilityBarrier : ICapabilityCatalogDurabilityBarrier
{
    public bool AttemptBeforeMove { get; init; }

    public bool SubstituteAfterMove { get; init; }

    public int BlockedBeforeMoveAttempts { get; private set; }

    public bool BeforeMoveSubstitutionSucceeded { get; private set; }

    public bool AfterMoveSubstitutionSucceeded { get; private set; }

    public void BeforeDirectoryMove(string stagingPath, string destinationPath)
    {
        _ = destinationPath;
        if (!AttemptBeforeMove)
        {
            return;
        }

        try
        {
            Directory.Move(stagingPath, stagingPath + ".substituted-before");
            Directory.CreateDirectory(stagingPath);
            BeforeMoveSubstitutionSucceeded = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            BlockedBeforeMoveAttempts++;
        }
    }

    public void AfterDirectoryMove(string stagingPath, string destinationPath)
    {
        _ = stagingPath;
        if (!SubstituteAfterMove || AfterMoveSubstitutionSucceeded)
        {
            return;
        }

        Directory.Move(destinationPath, destinationPath + ".substituted-after");
        Directory.CreateDirectory(destinationPath);
        AfterMoveSubstitutionSucceeded = true;
    }

    public void FlushAfterDirectoryCreate(string directoryPath, SafeFileHandle parentDirectory)
    {
        _ = directoryPath;
        _ = parentDirectory;
    }

    public ValueTask FlushAfterRenameAsync(string destinationPath, SafeFileHandle parentDirectory)
    {
        _ = destinationPath;
        _ = parentDirectory;
        return ValueTask.CompletedTask;
    }
}
