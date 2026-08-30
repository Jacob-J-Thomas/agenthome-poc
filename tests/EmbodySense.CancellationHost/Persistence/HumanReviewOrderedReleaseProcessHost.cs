using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanReviewOrderedReleaseProcessHost
{
    private const int ProcessLossExitCode = 179;

    internal static async Task<int> RunAsync(string workspaceRoot, string runId, string resultPath)
    {
        var paths = new WorkspacePaths(workspaceRoot);
        using var store = new CustomLoopRunStore(paths);
        var current = await store.GetAsync(runId);
        if (!HumanReviewOrderedReleaseProcessIntentFactory.TryCreate(current, out var intent, out var releaseAtUtc) || intent is null) return 2;

        var result = await new HumanReviewOrderedReleaseService(
            store,
            new HumanReviewOrderedReleaseProcessContextResolver(),
            new HumanReviewOrderedReleaseProcessRuntime(),
            new HumanReviewOrderedReleaseProcessClock(releaseAtUtc),
            new HumanReviewOrderedReleaseProcessAuthority()).ReleaseAsync(intent);
        await File.WriteAllTextAsync(resultPath, result.Status.ToString());
        return result.Status == HumanReviewDecisionActionReleaseStatus.Completed ? 0 : 3;
    }

    internal static async Task<int> RunProcessLossAsync(string workspaceRoot, string runId, string boundaryText)
    {
        if (!Enum.TryParse<CustomLoopRunPublicationBoundary>(boundaryText, out var boundary) || !Enum.IsDefined(boundary) || boundary == 0) return 2;

        var paths = new WorkspacePaths(workspaceRoot);
        using var store = new CustomLoopRunStore(paths, null, (currentBoundary, _) => ExitAfterBoundaryAsync(currentBoundary, boundary));
        var current = await store.GetAsync(runId);
        if (!HumanReviewOrderedReleaseProcessIntentFactory.TryCreate(current, out var intent, out var releaseAtUtc) || intent is null) return 3;

        var result = await new HumanReviewOrderedReleaseService(
            store,
            new HumanReviewOrderedReleaseProcessContextResolver(),
            new HumanReviewOrderedReleaseProcessRuntime(),
            new HumanReviewOrderedReleaseProcessClock(releaseAtUtc),
            new HumanReviewOrderedReleaseProcessAuthority()).ReleaseAsync(intent);
        return result.Status == HumanReviewDecisionActionReleaseStatus.Completed ? 0 : 4;
    }

    internal static async Task<int> RunRaceAsync(string workspaceRoot, string runId, string readyPath, string releasePath, string resultPath)
    {
        var paths = new WorkspacePaths(workspaceRoot);
        using var store = new CustomLoopRunStore(paths);
        var current = await store.GetAsync(runId);
        if (!HumanReviewOrderedReleaseProcessIntentFactory.TryCreate(current, out var intent, out var releaseAtUtc) || intent is null) return 2;

        await File.WriteAllTextAsync(readyPath, "ready");
        await WaitForFileAsync(releasePath, TimeSpan.FromSeconds(30));
        var result = await new HumanReviewOrderedReleaseService(
            store,
            new HumanReviewOrderedReleaseProcessContextResolver(),
            new HumanReviewOrderedReleaseProcessRuntime(),
            new HumanReviewOrderedReleaseProcessClock(releaseAtUtc),
            new HumanReviewOrderedReleaseProcessAuthority()).ReleaseAsync(intent);
        await File.WriteAllTextAsync(resultPath, result.Status.ToString());
        return result.Status == HumanReviewDecisionActionReleaseStatus.Completed ? 0 : 3;
    }

    private static ValueTask ExitAfterBoundaryAsync(CustomLoopRunPublicationBoundary currentBoundary, CustomLoopRunPublicationBoundary requestedBoundary)
    {
        if (currentBoundary == requestedBoundary)
        {
            Console.Error.WriteLine($"The test host process crashed after `{currentBoundary}`.");
            Console.Error.Flush();
            Environment.Exit(ProcessLossExitCode);
        }

        return ValueTask.CompletedTask;
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path)) await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
    }
}
