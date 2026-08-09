using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class SwappingCapabilityCatalogPathObserver(string sourcePath, string retainedPath, string replacementTargetPath) : ICapabilityCatalogPathObserver
{
    private bool _swapActive;

    public bool Attempted { get; private set; }

    public Exception? RejectedByOperatingSystem { get; private set; }

    public bool Swapped { get; private set; }

    public void BeforeDirectoryChildOpen(string parentPath, string childName)
    {
        _ = parentPath;
        _ = childName;
    }

    public void BeforeFileChildOpen(string parentPath, string childName)
    {
        if (Swapped || childName != "artifact.evidence.json" || !parentPath.StartsWith(sourcePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Attempted = true;
        try
        {
            Directory.Move(sourcePath, retainedPath);
            Directory.CreateSymbolicLink(sourcePath, replacementTargetPath);
            _swapActive = true;
            Swapped = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RejectedByOperatingSystem = exception;
            if (!Directory.Exists(sourcePath) && Directory.Exists(retainedPath))
            {
                try
                {
                    Directory.Move(retainedPath, sourcePath);
                }
                catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException)
                {
                    RejectedByOperatingSystem = new AggregateException(exception, restoreException);
                }
            }
            throw;
        }
    }

    public void AfterFileChildOpen(string parentPath, string childName)
    {
        _ = parentPath;
        _ = childName;
        if (!_swapActive)
        {
            return;
        }

        Directory.Delete(sourcePath);
        Directory.Move(retainedPath, sourcePath);
        _swapActive = false;
    }
}
