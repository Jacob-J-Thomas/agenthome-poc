using System.Diagnostics;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Persistence.Loops.Revisions.Models;
using EmbodySense.Core.Persistence.Tests.Loops.Revisions;

namespace EmbodySense.CancellationHost.Persistence;

internal static class GovernedLoopRevisionStoreCrossProcessHost
{
    internal static async Task<int> RunAsync(
        string mode,
        string workspaceRoot,
        string trustRoot,
        string gatePath,
        string readyPath,
        string outputPath,
        string graphId,
        string revisionId,
        string operationId,
        string requestHash)
    {
        if (mode is not ("writer" or "crash-primary"))
        {
            return 2;
        }

        await File.WriteAllTextAsync(readyPath, "ready");
        await WaitForPathAsync(gatePath);
        GovernedLoopRevisionStoreOptions? options = mode == "crash-primary"
            ? new GovernedLoopRevisionStoreOptions
            {
                DurableBoundaryObserver = (boundary, _) =>
                {
                    if (boundary == GovernedLoopRevisionPersistenceBoundary.PrimaryPublished)
                    {
                        TerminateProcess();
                    }

                    return ValueTask.CompletedTask;
                }
            }
            : null;
        var store = new GovernedLoopRevisionLifecycleStore(
            new WorkspacePaths(workspaceRoot),
            new FileCapabilityCatalogTrustProvider(trustRoot),
            options);
        var mutation = GovernedLoopRevisionLifecycleStoreTestData.CreateDraftMutation(graphId, revisionId, operationId, requestHash, 0);
        var retryWindow = Stopwatch.StartNew();
        GovernedLoopRevisionStoreCommitResult result;
        do
        {
            result = await store.CommitAsync(mutation);
            if (mode != "writer"
                || result.Status != GovernedLoopRevisionStoreCommitStatus.Unavailable
                || retryWindow.Elapsed >= TimeSpan.FromSeconds(15))
            {
                break;
            }

            await Task.Delay(50);
        }
        while (true);

        await File.WriteAllTextAsync(outputPath, result.Status.ToString());
        return 0;
    }

    private static async Task WaitForPathAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (wait.Elapsed >= TimeSpan.FromSeconds(15))
            {
                throw new TimeoutException($"The revision store host did not observe `{path}`.");
            }

            await Task.Delay(10);
        }
    }

    private static void TerminateProcess()
    {
        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    }
}
