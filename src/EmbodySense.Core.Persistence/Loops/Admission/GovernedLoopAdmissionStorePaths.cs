using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops.Admission;

internal sealed class GovernedLoopAdmissionStorePaths
{
    public GovernedLoopAdmissionStorePaths(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        RootPath = Path.Combine(paths.AgentPath, "loops", "admissions");
        PrimaryPath = Path.Combine(RootPath, "terminal-outcomes.json");
        ProofPath = Path.Combine(RootPath, "terminal-outcomes.proved.json");
        LockPath = Path.Combine(RootPath, ".mutations.lock");
    }

    public string RootPath { get; }

    public string PrimaryPath { get; }

    public string ProofPath { get; }

    public string LockPath { get; }
}
