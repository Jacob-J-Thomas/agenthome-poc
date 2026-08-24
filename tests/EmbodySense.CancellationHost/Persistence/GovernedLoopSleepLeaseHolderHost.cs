using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal static class GovernedLoopSleepLeaseHolderHost
{
    private static readonly TimeSpan _releaseTimeout = TimeSpan.FromSeconds(60);

    internal static async Task<int> RunAsync(
        string workspaceRoot,
        string checkpointId,
        string releaseMarker,
        string readyMarker,
        string resultMarker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        var store = new GovernedLoopSleepStore(
            new WorkspacePaths(workspaceRoot),
            new GovernedLoopSleepStoreOptions
            {
                MutationLockAcquiredObserver = _ =>
                    SignalReadyAndWaitForRelease(readyMarker, releaseMarker),
            });

        var result = await store.ReadCheckpointAsync(checkpointId);
        await CrossProcessMarkerProtocol.WriteResultAsync(resultMarker, result?.Status.ToString() ?? "Null");
        return 0;
    }

    private static void SignalReadyAndWaitForRelease(string readyMarker, string releaseMarker)
    {
        File.WriteAllText(readyMarker, "ready");
        var startedAt = TimeProvider.System.GetTimestamp();
        while (!File.Exists(releaseMarker))
        {
            if (TimeProvider.System.GetElapsedTime(startedAt) >= _releaseTimeout)
            {
                throw new TimeoutException($"The governed-loop sleep lease release marker was not published within {_releaseTimeout}.");
            }

            Thread.Sleep(10);
        }
    }
}
