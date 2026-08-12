using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.CancellationHost.Persistence;

internal static class CustomLoopSequentialEvidenceCrossProcessHost
{
    internal static async Task<int> ResolveAsync(string workspaceRoot, string evidenceHash, string resultPath)
    {
        using var store = new CustomLoopRunStore(new WorkspacePaths(workspaceRoot));
        if (await store.ResolveAsync(evidenceHash) is not GovernedLoopSequentialNodeEvidenceReceipt receipt)
        {
            return 3;
        }

        if (await ((IGovernedLoopSequentialRunEvidenceSource)store).ResolveAsync(receipt.RunId)
                is not GovernedLoopSequentialRunEvidence runEvidence
            || !string.Equals(
                receipt.RunId,
                runEvidence.AdapterBinding.ExecutionBinding.RunId,
                StringComparison.Ordinal)
            || !string.Equals(
                runEvidence.InvocationSnapshot.ContentHash,
                runEvidence.AdapterBinding.InvocationPayloadHash,
                StringComparison.Ordinal))
        {
            return 4;
        }

        await File.WriteAllTextAsync(resultPath, "resolved");
        return 0;
    }
}
