using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Policies;

internal sealed class HumanInputPolicyFileStorePathRaceObserver(string policyPath, string displacedPath, byte[] replacementBytes, int replacementOpen)
    : ICapabilityCatalogPathObserver
{
    private int _policyOpenCount;

    public bool Replaced { get; private set; }

    public int PolicyOpenCount => Volatile.Read(ref _policyOpenCount);

    public void BeforeDirectoryChildOpen(string parentPath, string childName)
    {
        _ = parentPath;
        _ = childName;
    }

    public void BeforeFileChildOpen(string parentPath, string childName)
    {
        if (Replaced || !string.Equals(Path.GetFileName(policyPath), childName, StringComparison.Ordinal) || !string.Equals(Path.GetDirectoryName(policyPath), parentPath, StringComparison.Ordinal))
        {
            return;
        }

        if (Interlocked.Increment(ref _policyOpenCount) != replacementOpen)
        {
            return;
        }

        File.Move(policyPath, displacedPath);
        File.WriteAllBytes(policyPath, replacementBytes);
        Replaced = true;
    }

    public void AfterFileChildOpen(string parentPath, string childName)
    {
        _ = parentPath;
        _ = childName;
    }
}
