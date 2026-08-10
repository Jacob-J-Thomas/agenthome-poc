using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops.Revisions;

internal sealed class GovernedLoopRevisionStorePaths
{
    public GovernedLoopRevisionStorePaths(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        RootPath = Path.Combine(paths.AgentPath, "loops", "revisions");
        PrimaryPath = Path.Combine(RootPath, "lifecycle.json");
        ProofPath = Path.Combine(RootPath, "lifecycle.proved.json");
        LockPath = Path.Combine(RootPath, ".mutations.lock");
    }

    public string RootPath { get; }

    public string PrimaryPath { get; }

    public string ProofPath { get; }

    public string LockPath { get; }
}
