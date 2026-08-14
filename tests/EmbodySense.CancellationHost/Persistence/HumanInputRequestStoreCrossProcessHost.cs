using System.Diagnostics;
using System.Globalization;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.HumanInput.Requests.Models;
using static EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputRequestStoreTestData;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanInputRequestStoreCrossProcessHost
{
    internal static async Task<int> RunAsync(
        string mode,
        string workspaceRoot,
        string trustRoot,
        string gatePath,
        string readyPath,
        string outputPath,
        string requestId,
        string operationId,
        string requestHash,
        string boundaryText,
        string generationText,
        string relatedRequestId)
    {
        if (mode is not ("writer" or "related-reader" or "crash")
            || !long.TryParse(generationText, NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
        {
            return 2;
        }

        await File.WriteAllTextAsync(readyPath, "ready");
        await WaitForPathAsync(gatePath);
        HumanInputRequestStoreOptions? options = null;
        if (mode == "crash")
        {
            if (!Enum.TryParse<HumanInputRequestPersistenceBoundary>(boundaryText, out var boundary))
            {
                return 2;
            }

            options = new HumanInputRequestStoreOptions
            {
                DurableBoundaryObserver = (observed, _) =>
                {
                    if (observed == boundary)
                    {
                        TerminateProcess();
                    }

                    return ValueTask.CompletedTask;
                }
            };
        }

        var store = new HumanInputRequestStore(
            new WorkspacePaths(workspaceRoot),
            new FileCapabilityCatalogTrustProvider(trustRoot),
            options);
        if (mode == "related-reader")
        {
            var read = await store.ReadForMutationAsync(requestId, operationId, requestHash, EmptyToNull(relatedRequestId));
            await File.WriteAllTextAsync(
                outputPath,
                $"{read.Status}|{read.StoreGeneration.ToString(CultureInfo.InvariantCulture)}|{(read.RelatedSnapshot is not null).ToString(CultureInfo.InvariantCulture)}");
            return 0;
        }

        var mutation = CreateMutation(
            requestId,
            requestId == "request-one" ? "version-one" : "version-two",
            operationId,
            requestHash,
            generation);
        var retryWindow = Stopwatch.StartNew();
        HumanInputRequestLifecycleStoreCommitResult result;
        do
        {
            result = await store.CommitAsync(mutation);
            if (mode != "writer"
                || result.Status != HumanInputRequestLifecycleStoreCommitStatus.Unavailable
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

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private static async Task WaitForPathAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (wait.Elapsed >= TimeSpan.FromSeconds(15))
            {
                throw new TimeoutException($"The Human Input store host did not observe `{path}`.");
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
