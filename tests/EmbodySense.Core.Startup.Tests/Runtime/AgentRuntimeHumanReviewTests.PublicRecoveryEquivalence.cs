using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;
using EmbodySense.Tests.Support;
using CommonCustomLoopRunStatus = EmbodySense.Core.Common.Loops.Models.Custom.Execution.CustomLoopRunStatus;
using CommonGovernedLoopFrontierStatus = EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopFrontierStatus;

using static EmbodySense.Core.Startup.Tests.Runtime.AgentRuntimeFactoryTests;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeHumanReviewTests
{
    [Fact]
    public async Task Background_recovery_advances_across_public_bounded_pages_without_duplicate_release_after_restart()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var runIds = Enumerable.Range(0, CustomLoopLimits.MaxRecentRunsPageSize + 2)
            .Select(index => $"run-public-recovery-page-{index:D3}")
            .ToArray();
        var candidateIds = runIds[^2..];
        var paths = new WorkspacePaths(workspace.RootPath);

        using (var seedStore = new CustomLoopRunStore(paths))
        {
            foreach (var runId in runIds[..^2])
            {
                var admitted = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync(runId, "admission-" + runId, "public-recovery-loop-" + runId[25..]);
                await SeedAdmittedRunAsync(seedStore, admitted);
            }

            foreach (var runId in candidateIds)
            {
                var approved = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync(runId, "admission-" + runId, "public-recovery-loop-" + runId[25..]);
                await SeedApprovedRunAsync(seedStore, approved);
            }
        }

        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using var runStoreProvider = new CustomLoopRunStoreProvider(workspace.RootPath);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                new RejectingApprovalPrompt(),
                workspace.ServerStatePath,
                CreateCompatibleRuntimeStatus(executablePath))
            .WithCustomLoopRunStoreProvider(runStoreProvider)
            .WithHumanReviewDecisionAuthorizationProvider(new HumanReviewDecisionAuthorizationProviderTestDouble());

        Dictionary<string, RecoveryFingerprint> beforeRestart;
        await using (var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web))
        {
            var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, start.Status);
            beforeRestart = await WaitForPublishedRecoveryAsync(paths, candidateIds);
            Assert.All(candidateIds, runId => Assert.Equal(HumanReviewContinuationStatus.Published, beforeRestart[runId].ContinuationStatus));
            Assert.Equal(candidateIds.Length, beforeRestart.Count);

            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, (await runtime.StopGovernedLoopLocalBackgroundAsync()).Status);

            var restart = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, restart.Status);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            var afterRestart = await ReadRecoveryFingerprintsAsync(paths, candidateIds);

            Assert.Equal(beforeRestart, afterRestart);
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, (await runtime.StopGovernedLoopLocalBackgroundAsync()).Status);
        }
    }

    [Fact]
    public async Task Concurrent_public_background_starts_produce_one_local_owner_and_no_duplicate_activation()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var first = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        await using var second = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        var starts = await Task.WhenAll(
            first.StartGovernedLoopLocalBackgroundWithStatusAsync(),
            second.StartGovernedLoopLocalBackgroundWithStatusAsync());

        Assert.Equal(1, starts.Count(result => result.Status == AgentRuntimeGovernedLoopBackgroundStartStatus.Started));
        Assert.Equal(1, starts.Count(result => result.Status == AgentRuntimeGovernedLoopBackgroundStartStatus.OwnedByLivePeer));
        var firstStatus = await first.ReadGovernedLoopLocalBackgroundStatusAsync();
        var secondStatus = await second.ReadGovernedLoopLocalBackgroundStatusAsync();
        Assert.Equal(1, new[] { firstStatus, secondStatus }.Count(status => status.Ownership == AgentRuntimeGovernedLoopBackgroundOwnership.Local));
        Assert.Equal(1, new[] { firstStatus, secondStatus }.Count(status => status.Ownership == AgentRuntimeGovernedLoopBackgroundOwnership.LivePeer));

        var firstStop = await first.StopGovernedLoopLocalBackgroundAsync();
        var secondStop = await second.StopGovernedLoopLocalBackgroundAsync();
        Assert.True(firstStop.Status is AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped or AgentRuntimeGovernedLoopBackgroundStopStatus.OwnedByLivePeer);
        Assert.True(secondStop.Status is AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped or AgentRuntimeGovernedLoopBackgroundStopStatus.OwnedByLivePeer or AgentRuntimeGovernedLoopBackgroundStopStatus.AlreadyStopped);
    }

    [Fact]
    public async Task Background_start_maps_a_corrupt_live_recovery_record_to_unavailable_without_dispatch()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        const string RunId = "run-public-recovery-corrupt-live";
        var approved = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync(RunId, "admission-public-recovery-corrupt-live", "public-recovery-corrupt-loop");
        var paths = new WorkspacePaths(workspace.RootPath);
        using (var seedStore = new CustomLoopRunStore(paths))
        {
            await SeedApprovedRunAsync(seedStore, approved);
        }

        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopRunsPath, approved.LoopId, RunId + ".json"), "{ malformed");
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using var runStoreProvider = new CustomLoopRunStoreProvider(workspace.RootPath);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                new RejectingApprovalPrompt(),
                workspace.ServerStatePath,
                CreateCompatibleRuntimeStatus(executablePath))
            .WithCustomLoopRunStoreProvider(runStoreProvider)
            .WithHumanReviewDecisionAuthorizationProvider(new HumanReviewDecisionAuthorizationProviderTestDouble());

        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);
        var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var status = await runtime.ReadGovernedLoopLocalBackgroundStatusAsync();
        var stop = await runtime.StopGovernedLoopLocalBackgroundAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Unavailable, start.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable, start.Readiness);
        Assert.False(start.RetryAllowed);
        Assert.Contains("custom_loop_recovery_failed", start.Detail, StringComparison.Ordinal);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Stopped, status.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.AlreadyStopped, stop.Status);
        Assert.NotEqual(HumanReviewReadStatus.Ready, (await runtime.HumanReview.ReadAsync(RunId)).Status);
    }

    [Fact]
    public async Task Background_recovery_skips_a_public_trace_tombstone_without_claiming_human_review_work()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        LoopRunSnapshot completed;
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using (var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, executablePath))
        {
            var created = Assert.IsType<LoopDefinitionSnapshot>((await runtime.LoopAuthoring.CreateAsync("create-public-recovery-tombstone-loop")).Definition);
            var invocation = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(created.Id, created.DefinitionVersion, created.ContentHash, "invoke-public-recovery-tombstone-loop", "complete this bounded test loop"));
            completed = Assert.IsType<LoopRunSnapshot>(invocation.Run);
            Assert.Equal("Completed", invocation.ExecutionStatus);
        }

        var paths = new WorkspacePaths(workspace.RootPath);
        using (var store = new CustomLoopRunStore(paths))
        {
            var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(completed.Id));
            var request = new CustomLoopTraceDeletionRequest(completed.Id, inspection.PersistedArtifactHash, "delete-public-recovery-tombstone", "actor-user", "web");
            var mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), DateTimeOffset.UtcNow.ToUniversalTime());
            Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(mutation)).Status);
            Assert.NotNull(Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(completed.Id)).Tombstone);
            Assert.Null(await store.GetAsync(completed.Id));
        }

        await using var recoveryRuntime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, executablePath);
        var start = await recoveryRuntime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var review = await recoveryRuntime.HumanReview.ReadAsync(completed.Id);

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, start.Status);
        Assert.Equal(HumanReviewReadStatus.NotFound, review.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, (await recoveryRuntime.StopGovernedLoopLocalBackgroundAsync()).Status);
    }

    [Fact]
    public async Task Cancelled_public_background_start_propagates_and_keeps_human_review_non_executable()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.StartGovernedLoopLocalBackgroundWithStatusAsync(cancellation.Token));
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var humanReview = Assert.Single(catalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);

        Assert.False(humanReview.IsExecutable);
        Assert.NotEqual(AgentRuntimeGovernedLoopBackgroundReadiness.Ready, (await runtime.ReadGovernedLoopLocalBackgroundStatusAsync()).Readiness);
    }

    private static async Task SeedApprovedRunAsync(CustomLoopRunStore store, CustomLoopRunRecord approved)
    {
        var admitted = CreateAdmittedRun(approved);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var running = CreateRunning(admitted, approved);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, admitted.LifecycleVersion)).Status);
        var started = CreateStarted(running, approved);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(started, running.LifecycleVersion)).Status);
        var admission = await new HumanReviewAdmissionService(store).AdmitAsync(new HumanReviewAdmissionCommand(started.Id, started.LifecycleVersion, approved.HumanReview!.Request, approved.Frontier!, approved.Events[3]));
        Assert.Equal(CustomLoopRunStoreStatus.Updated, admission.Status);
        var accepted = approved.HumanReview.AcceptedTerminalDecision ?? throw new InvalidOperationException("The canonical recovery test run did not retain its approval decision.");
        var paused = await store.GetAsync(started.Id) ?? throw new InvalidOperationException("The canonical recovery test run was not persisted after admission.");
        var decision = await new HumanReviewDecisionService(store, new HumanReviewRecoveryServerAuthorizer(), new HumanReviewRecoveryTrustedClock(paused.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new HumanReviewDecisionCommand(started.Id, paused.LifecycleVersion, accepted.DecisionOperationId, accepted.Kind, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, decision.Status);
    }

    private static async Task SeedAdmittedRunAsync(CustomLoopRunStore store, CustomLoopRunRecord approved)
    {
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(CreateAdmittedRun(approved))).Status);
    }

    private static CustomLoopRunRecord CreateAdmittedRun(CustomLoopRunRecord approved)
    {
        var finalFrontier = approved.Frontier ?? throw new InvalidOperationException("The canonical recovery test run did not retain a frontier.");
        var review = finalFrontier.Payload.Nodes.Single(node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        var initialReview = GovernedLoopNodeExecutionEvidence.CreateActivation(
            review.ActivationOrdinal,
            review.PlanOrdinal,
            review.VisitOrdinal,
            review.NodeId,
            review.Descriptor,
            review.IncomingControlEdgeIds,
            review.OutgoingControlEdgeIds,
            GovernedLoopNodeExecutionStatus.Ready);
        var initialFrontier = GovernedLoopFrontierPosture.Create(
            finalFrontier.Binding,
            finalFrontier.WorkspaceId,
            finalFrontier.GraphArtifactHash,
            finalFrontier.GraphLayoutHash,
            finalFrontier.AdmissionReceiptHash,
            1,
            finalFrontier.Payload.ConcurrencyCeiling,
            CommonGovernedLoopFrontierStatus.Active,
            [finalFrontier.Payload.Nodes[0], initialReview],
            approved.CreatedAtUtc,
            string.Empty);
        var admitted = approved with
        {
            LifecycleVersion = 1,
            Status = CommonCustomLoopRunStatus.Admitted,
            UpdatedAtUtc = approved.CreatedAtUtc,
            CompletedAtUtc = null,
            ExecutionClock = CustomLoopExecutionClock.NotStarted(),
            Events = approved.Events.Take(2).ToArray(),
            Frontier = initialFrontier,
            Checkpoint = CustomLoopRunCheckpoint.Start(),
            HumanReview = null,
            WaitEvidence = [],
            HumanInputWaitingCheckpoints = [],
            FinalOutput = null,
            FailureCode = null,
            FailureDetail = null
        };
        Assert.True(CustomLoopRunValidator.Validate(admitted).IsValid);
        return admitted;
    }

    private static async Task<Dictionary<string, RecoveryFingerprint>> WaitForPublishedRecoveryAsync(WorkspacePaths paths, IReadOnlyList<string> runIds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var current = await ReadRecoveryFingerprintsAsync(paths, runIds);
            if (current.Count == runIds.Count && current.Values.All(item => item.ContinuationStatus == HumanReviewContinuationStatus.Published))
            {
                return current;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }

    private static async Task<Dictionary<string, RecoveryFingerprint>> ReadRecoveryFingerprintsAsync(WorkspacePaths paths, IReadOnlyList<string> runIds)
    {
        using var store = new CustomLoopRunStore(paths);
        var result = new Dictionary<string, RecoveryFingerprint>(StringComparer.Ordinal);
        foreach (var runId in runIds)
        {
            var run = await store.GetAsync(runId);
            if (run?.HumanReview?.Continuation is not { } continuation)
            {
                continue;
            }

            result[runId] = new(
                run.LifecycleVersion,
                run.Events.Length,
                continuation.StateHash,
                continuation.Claims.Length == 0
                    ? HumanReviewContinuationStatus.Published
                    : HumanReviewContinuationStatus.Claimed);
        }

        return result;
    }

    private sealed record RecoveryFingerprint(int LifecycleVersion, int EventCount, string ContinuationStateHash, HumanReviewContinuationStatus ContinuationStatus);
}
