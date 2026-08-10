using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.HumanInput.Requests;

internal sealed class HumanInputRequestStorePaths
{
    public HumanInputRequestStorePaths(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        RootPath = Path.Combine(paths.AgentPath, "human-input", "requests");
        PrimaryPath = Path.Combine(RootPath, "lifecycle.json");
        ProofPath = Path.Combine(RootPath, "lifecycle.proved.json");
        LockPath = Path.Combine(RootPath, ".mutations.lock");
    }

    public string RootPath { get; }

    public string PrimaryPath { get; }

    public string ProofPath { get; }

    public string LockPath { get; }
}
