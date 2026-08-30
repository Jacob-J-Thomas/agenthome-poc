using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;

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

    internal static async Task<int> ParkEffectAsync(string workspaceRoot, string runId, string markerPath, string resultPath)
    {
        var paths = new WorkspacePaths(workspaceRoot);
        using var store = new CustomLoopRunStore(paths);
        var current = await store.GetAsync(runId);
        var context = current is null ? null : await new HumanReviewOrderedReleaseProcessContextResolver().ResolveAsync(current);
        if (current is null || context is null) return 2;
        var timeProvider = new HumanReviewOrderedReleaseProcessTimeProvider(current.UpdatedAtUtc);
        var runtime = HumanReviewOrderedReleaseProcessRuntimeFactory.Create(store, paths, markerPath, timeProvider, crashAfterMarker: false);
        var result = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));
        await File.WriteAllTextAsync(resultPath, result.Status.ToString());
        if (result.Status != CustomLoopOrderedRunStatus.Paused)
        {
            var retained = await store.GetAsync(runId);
            Console.Error.WriteLine($"Effect parking stopped with `{result.Status}`: {result.Detail}; failure={retained?.FailureCode}:{retained?.FailureDetail}.");
        }
        return result.Status == CustomLoopOrderedRunStatus.Paused ? 0 : 3;
    }

    internal static Task<int> RunEffectAsync(string workspaceRoot, string runId, string markerPath, string resultPath)
        => ReleaseEffectAsync(workspaceRoot, runId, markerPath, resultPath, crashAfterMarker: false, publicationBoundary: null);

    internal static Task<int> RunEffectResponseLossAsync(string workspaceRoot, string runId, string markerPath)
        => ReleaseEffectAsync(workspaceRoot, runId, markerPath, null, crashAfterMarker: true, publicationBoundary: null);

    internal static Task<int> RunEffectProcessLossAsync(string workspaceRoot, string runId, string markerPath, string boundaryText)
    {
        if (!Enum.TryParse<CustomLoopRunPublicationBoundary>(boundaryText, out var boundary) || !Enum.IsDefined(boundary) || boundary == 0) return Task.FromResult(2);
        return ReleaseEffectAsync(workspaceRoot, runId, markerPath, null, crashAfterMarker: false, boundary);
    }

    internal static async Task<int> RunEffectRaceAsync(string workspaceRoot, string runId, string markerPath, string readyPath, string releasePath, string resultPath)
    {
        var paths = new WorkspacePaths(workspaceRoot);
        using var store = new CustomLoopRunStore(paths);
        var attempts = new GovernedLoopEffectAttemptStore(paths);
        var (current, intent) = await CreateEffectIntentWithRetryAsync(store, attempts, runId);
        if (current is null || intent is null) return 2;

        await File.WriteAllTextAsync(readyPath, "ready");
        await WaitForFileAsync(releasePath, TimeSpan.FromSeconds(30));
        return await ReleaseEffectAsync(paths, store, attempts, current, intent, markerPath, resultPath, crashAfterMarker: false);
    }

    internal static async Task<int> RunEffectOwnerBarrierAsync(string workspaceRoot, string runId, string markerPath, string ownerReadyPath, string ownerReleasePath, string resultPath)
    {
        var paths = new WorkspacePaths(workspaceRoot);
        using var store = new CustomLoopRunStore(paths);
        var attempts = new GovernedLoopEffectAttemptStore(paths);
        var (current, intent) = await CreateEffectIntentWithRetryAsync(store, attempts, runId);
        if (current is null || intent is null) return 2;
        return await ReleaseEffectAsync(paths, store, attempts, current, intent, markerPath, resultPath, crashAfterMarker: false, ownerReadyPath, ownerReleasePath);
    }

    private static async Task<int> ReleaseEffectAsync(string workspaceRoot, string runId, string markerPath, string? resultPath, bool crashAfterMarker, CustomLoopRunPublicationBoundary? publicationBoundary)
    {
        var paths = new WorkspacePaths(workspaceRoot);
        using var store = publicationBoundary is { } boundary
            ? new CustomLoopRunStore(paths, null, (currentBoundary, _) => ExitAfterBoundaryAsync(currentBoundary, boundary))
            : new CustomLoopRunStore(paths);
        var attempts = new GovernedLoopEffectAttemptStore(paths);
        var (current, intent) = await CreateEffectIntentWithRetryAsync(store, attempts, runId);
        if (current is null || intent is null) return 2;

        return await ReleaseEffectAsync(paths, store, attempts, current, intent, markerPath, resultPath, crashAfterMarker);
    }

    private static async Task<int> ReleaseEffectAsync(
        WorkspacePaths paths,
        CustomLoopRunStore store,
        GovernedLoopEffectAttemptStore attempts,
        CustomLoopRunRecord current,
        HumanReviewOrderedReleaseProcessEffectIntent intent,
        string markerPath,
        string? resultPath,
        bool crashAfterMarker,
        string? ownerReadyPath = null,
        string? ownerReleasePath = null)
    {
        var timeProvider = new HumanReviewOrderedReleaseProcessTimeProvider(intent.ReleaseAtUtc);
        var runtime = HumanReviewOrderedReleaseProcessRuntimeFactory.Create(store, paths, markerPath, timeProvider, crashAfterMarker, ownerReadyPath, ownerReleasePath);
        var evidence = new CanonicalHumanReviewEffectEvidenceSource(store, attempts);
        var result = await new HumanReviewOrderedReleaseService(
            store,
            new HumanReviewOrderedReleaseProcessContextResolver(),
            runtime,
            timeProvider,
            new HumanReviewOrderedReleaseProcessAuthority(),
            evidence,
            evidence).ReleaseAsync(intent.Action, intent.Completion);
        if (resultPath is not null)
        {
            await File.WriteAllTextAsync(resultPath, result.Status.ToString());
        }
        return result.Status == HumanReviewContinuationReleaseStatus.Completed ? 0 : 3;
    }

    private static async Task<(CustomLoopRunRecord? Run, HumanReviewOrderedReleaseProcessEffectIntent? Intent)> CreateEffectIntentWithRetryAsync(CustomLoopRunStore store, GovernedLoopEffectAttemptStore attempts, string runId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var current = await store.GetAsync(runId);
            var intent = await HumanReviewOrderedReleaseProcessEffectIntentFactory.CreateAsync(current, attempts);
            if (current is not null && intent is not null) return (current, intent);
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        return (null, null);
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
