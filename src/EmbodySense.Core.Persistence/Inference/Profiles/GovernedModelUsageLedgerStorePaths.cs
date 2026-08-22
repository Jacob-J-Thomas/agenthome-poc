using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Inference.Profiles;

internal sealed class GovernedModelUsageLedgerStorePaths
{
    internal GovernedModelUsageLedgerStorePaths(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        RootPath = Path.Combine(paths.AgentPath, "loops", "execution", "model-usage");
        SegmentRootPath = Path.Combine(RootPath, "segments");
        PrimaryPath = Path.Combine(RootPath, "ledger.json");
        ProofPath = Path.Combine(RootPath, "ledger.proved.json");
        LockPath = Path.Combine(RootPath, ".ledger.lock");
    }

    internal string RootPath { get; }
    internal string SegmentRootPath { get; }
    internal string PrimaryPath { get; }
    internal string ProofPath { get; }
    internal string LockPath { get; }

    internal string SegmentPath(long segmentIndex)
    {
        if (segmentIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        }

        return Path.Combine(SegmentRootPath, $"segment-{segmentIndex:D20}.json");
    }
}
