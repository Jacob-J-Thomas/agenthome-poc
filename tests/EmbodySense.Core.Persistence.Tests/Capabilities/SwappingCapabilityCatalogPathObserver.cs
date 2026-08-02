using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class SwappingCapabilityCatalogPathObserver(string sourcePath, string retainedPath, string replacementTargetPath) : ICapabilityCatalogPathObserver
{
    private bool _swapActive;

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

        Directory.Move(sourcePath, retainedPath);
        Directory.CreateSymbolicLink(sourcePath, replacementTargetPath);
        _swapActive = true;
        Swapped = true;
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
