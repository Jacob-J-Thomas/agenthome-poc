using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Services;
using CommonCustomLoopRunStatus = EmbodySense.Core.Common.Loops.Models.Custom.Execution.CustomLoopRunStatus;

namespace EmbodySense.Web.Tests;

[Collection(EphemeralPortApiCollection.Name)]
public sealed class WebAgentRuntimeHostHumanReviewTests
{
    [Fact]
    public async Task HumanReview_wrappers_reuse_one_runtime_and_one_store_boundary()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var approvals = new WebApprovalCoordinator();
        var factoryCalls = 0;
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath]);
        await using var host = new WebAgentRuntimeHost(
            options,
            approvals,
            WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath),
            null,
            runtimeStatus =>
            {
                Interlocked.Increment(ref factoryCalls);
                return AgentRuntimeFactory.ForFileCapabilityTrustRoot(approvals, workspace.ServerStatePath, runtimeStatus);
            });
        await host.InitializeWorkspaceAsync();

        var page = await host.ListHumanReviewsAsync(new HumanReviewPageRequest(5));
        var read = await host.ReadHumanReviewAsync("run-missing");
        var evidence = await host.ReadHumanReviewEvidenceAsync("run-missing");
        var posture = await host.ReadHumanReviewPostureAsync("run-missing");
        var decision = await host.DecideHumanReviewAsync(new HumanReviewDecisionOperationInput("run-missing", 1, "operation-missing", HumanReviewDecisionKind.Approve, null));

        Assert.Equal(HumanReviewPageStatus.Ready, page.Status);
        Assert.Empty(page.Items);
        Assert.Equal(HumanReviewReadStatus.NotFound, read.Status);
        Assert.Equal(HumanReviewEvidenceReadStatus.NotFound, evidence.Status);
        Assert.Equal(HumanReviewReadStatus.NotFound, posture.Status);
        Assert.Equal(HumanReviewDecisionStatus.NotFound, decision.Status);
        Assert.Equal(1, Volatile.Read(ref factoryCalls));
    }

    [Fact]
    public async Task HumanReview_wrapper_requires_initialized_workspace_before_runtime_creation()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var approvals = new WebApprovalCoordinator();
        var factoryCalls = 0;
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath]);
        await using var host = new WebAgentRuntimeHost(
            options,
            approvals,
            WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath),
            null,
            runtimeStatus =>
            {
                Interlocked.Increment(ref factoryCalls);
                return AgentRuntimeFactory.ForFileCapabilityTrustRoot(approvals, workspace.ServerStatePath, runtimeStatus);
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ListHumanReviewsAsync(new HumanReviewPageRequest()));

        Assert.Contains("Workspace is not initialized", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref factoryCalls));
    }

    [Fact]
    public async Task HumanReview_wrapper_honors_caller_cancellation_before_acquiring_runtime()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var approvals = new WebApprovalCoordinator();
        var factoryCalls = 0;
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath]);
        await using var host = new WebAgentRuntimeHost(
            options,
            approvals,
            WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath),
            null,
            runtimeStatus =>
            {
                Interlocked.Increment(ref factoryCalls);
                return AgentRuntimeFactory.ForFileCapabilityTrustRoot(approvals, workspace.ServerStatePath, runtimeStatus);
            });
        await host.InitializeWorkspaceAsync();

        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ListHumanReviewsAsync(new HumanReviewPageRequest(), callerCancellation.Token));
        Assert.Equal(0, Volatile.Read(ref factoryCalls));
    }

    [Fact]
    public async Task HumanReview_wrapper_runs_durable_recovery_before_the_first_facade_operation()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var approvals = new WebApprovalCoordinator();
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath]);
        await using var host = new WebAgentRuntimeHost(options, approvals, WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath));
        await host.InitializeWorkspaceAsync();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var interrupted = CreateRunningRun("run-human-review-recovery-boundary");
        await PersistRunningRunAsync(store, interrupted);

        var response = await host.ReadHumanReviewAsync("run-missing");
        var recovered = await host.GetLoopRunAsync(interrupted.Id);

        Assert.Equal(HumanReviewReadStatus.NotFound, response.Status);
        Assert.NotNull(recovered);
        Assert.Equal("Paused", recovered.Status);
    }

    private static CustomLoopRunRecord CreateRunningRun(string runId)
    {
        var now = DateTimeOffset.Parse("2026-07-26T12:00:00+00:00");
        var definition = CustomLoopDefinitionContentHash.Apply(CustomLoopDefinition.CreateSeed("loop-web-human-review-recovery", "role-workspace", "step-only", "create-loop-web-human-review-recovery", now) with { ContentHash = string.Empty });
        CustomLoopRunEvent[] events =
        [
            new(1, $"admitted-{runId}", now, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null),
            new(2, $"admission-audit-{runId}", now, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "Admission audit completed.", [], null, null, null, null, null, null, null, null, null, null),
            new(3, $"running-{runId}", now.AddSeconds(1), CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered Running.", [], null, null, null, null, null, null, null, null, null, null)
        ];
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            runId,
            definition.Id,
            events.Length,
            CommonCustomLoopRunStatus.Running,
            now,
            now.AddSeconds(1),
            null,
            "web",
            new CustomLoopModelSnapshot("provider", "model"),
            $"admit-{runId}",
            WorkspaceActors.Web,
            string.Empty,
            definition,
            "prompt",
            null,
            CustomLoopContextSnapshot.CreateEmpty(now),
            new CustomLoopExecutionClock(0, now.AddSeconds(1)),
            CustomLoopRunCheckpoint.Start(),
            events,
            null,
            null,
            null)
        {
            CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, now)
        };
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static async Task PersistRunningRunAsync(CustomLoopRunStore store, CustomLoopRunRecord running)
    {
        var admitted = running with
        {
            LifecycleVersion = 1,
            Status = CommonCustomLoopRunStatus.Admitted,
            UpdatedAtUtc = running.CreatedAtUtc,
            ExecutionClock = CustomLoopExecutionClock.NotStarted(),
            Events = [running.Events[0]]
        };
        var audited = admitted with
        {
            LifecycleVersion = 2,
            Events = [.. running.Events[..2]]
        };

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(audited, admitted.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, audited.LifecycleVersion)).Status);
    }
}
