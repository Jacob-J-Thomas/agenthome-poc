using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops.Execution.Authority;

internal sealed class GovernedLoopEffectAuthorityEvidenceStorePaths
{
    public GovernedLoopEffectAuthorityEvidenceStorePaths(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        RootPath = Path.Combine(paths.AgentPath, "loops", "effect-authority");
        PrimaryPath = Path.Combine(RootPath, "decisions.json");
        ProofPath = Path.Combine(RootPath, "decisions.proved.json");
        LockPath = Path.Combine(RootPath, ".mutations.lock");
    }

    public string RootPath { get; }

    public string PrimaryPath { get; }

    public string ProofPath { get; }

    public string LockPath { get; }
}
