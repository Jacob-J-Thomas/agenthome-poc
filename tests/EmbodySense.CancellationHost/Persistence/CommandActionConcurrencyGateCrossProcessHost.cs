using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.CommandActions;

namespace EmbodySense.CancellationHost.Persistence;

internal static class CommandActionConcurrencyGateCrossProcessHost
{
    internal static async Task<int> RunAsync(
        string workspaceRoot,
        string templateHash,
        string maximumConcurrencyText,
        string readyMarker,
        string releaseMarker)
    {
        if (!int.TryParse(maximumConcurrencyText, out var maximumConcurrency) || maximumConcurrency is < 1 or > 64)
        {
            return 2;
        }

        var gate = new CommandActionConcurrencyGate(new WorkspacePaths(workspaceRoot));
        await using var lease = await gate.TryAcquireAsync(templateHash, maximumConcurrency, TimeSpan.FromSeconds(10));
        if (lease is null)
        {
            return 3;
        }

        await CrossProcessMarkerProtocol.SignalReadyAndWaitForReleaseAsync(readyMarker, releaseMarker);
        return 0;
    }
}
