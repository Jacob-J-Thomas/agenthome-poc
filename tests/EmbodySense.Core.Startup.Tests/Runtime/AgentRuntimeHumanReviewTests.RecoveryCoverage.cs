using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

using static EmbodySense.Core.Startup.Tests.Runtime.AgentRuntimeFactoryTests;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeHumanReviewTests
{
    [Fact]
    public async Task Human_review_readiness_accepts_a_valid_persisted_graph_snapshot()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await InitializeHumanReviewAuthorityAsync(workspace);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
            new RejectingApprovalPrompt(),
            workspace.ServerStatePath,
            CreateCompatibleRuntimeStatus(executablePath))
            .WithHumanReviewDecisionAuthorizationProvider(new HumanReviewDecisionAuthorizationProviderTestDouble());

        await using var runtime = await factory.CreateAsync(
            "test-model",
            workspace.RootPath,
            executablePath,
            "read-only",
            AgentRuntimeSurface.Web);

        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var role = Assert.Single(catalog.Roles.Roles, item => item.IsAdmissionReady);
        var candidate = BrowserGraphCandidate(new ContextualRoleRevisionPin(
            new ContextualRoleRevisionIdentity(role.RoleId, role.Revision),
            role.ContentHash));
        var created = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "create-human-review-readiness-graph",
            GovernedLoopGraphMutationKind.CreateDraft,
            candidate.GraphId!,
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            candidate));
        var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var descriptor = Assert.Single(
            (await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync()).NodeDescriptors,
            item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);

        Assert.Equal("committed", created.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, start.Status);
        Assert.True(descriptor.IsExecutable);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, (await runtime.StopGovernedLoopLocalBackgroundAsync()).Status);
    }

    [Fact]
    public async Task Background_legacy_activation_projects_a_live_peer_without_claiming_ownership()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var owner = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        await using var peer = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        var ownerStart = await owner.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var peerStart = await peer.StartGovernedLoopLocalBackgroundAsync();
        var peerStatus = await peer.ReadGovernedLoopLocalBackgroundStatusAsync();
        var peerStop = await peer.StopGovernedLoopLocalBackgroundAsync();
        var ownerStop = await owner.StopGovernedLoopLocalBackgroundAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, ownerStart.Status);
        Assert.True(peerStart.Available);
        Assert.Equal("Available", peerStart.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.LivePeer, peerStatus.Ownership);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.OwnedByLivePeer, peerStop.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, ownerStop.Status);
    }

    [Fact]
    public Task Background_disposal_retains_durable_peer_evidence_after_ownership_loss()
        => EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTests.Public_background_dispose_parks_a_hostile_local_provider_after_peer_handoff();

    [Fact]
    public async Task Background_stop_and_wait_are_idempotent_before_and_after_activation()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        var initialWait = await runtime.WaitForGovernedLoopLocalBackgroundStopAsync();
        var initialStatus = await runtime.ReadGovernedLoopLocalBackgroundStatusAsync();
        var stopped = await runtime.StopGovernedLoopLocalBackgroundAsync();
        var started = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var repeatedStart = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var legacyRepeatedStart = await runtime.StartGovernedLoopLocalBackgroundAsync();
        var activeStatus = await runtime.ReadGovernedLoopLocalBackgroundStatusAsync();
        var drained = await runtime.StopGovernedLoopLocalBackgroundAsync();
        var completedWait = await runtime.WaitForGovernedLoopLocalBackgroundStopAsync();
        var repeatedWait = await runtime.WaitForGovernedLoopLocalBackgroundStopAsync();
        var finalStatus = await runtime.ReadGovernedLoopLocalBackgroundStatusAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.AlreadyStopped, initialWait.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Stopped, initialWait.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.None, initialWait.Ownership);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Stopped, initialStatus.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.AlreadyStopped, stopped.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, started.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.AlreadyRunning, repeatedStart.Status);
        Assert.True(legacyRepeatedStart.Available);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Ready, activeStatus.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, drained.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, completedWait.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, repeatedWait.Status);
        Assert.Equal(completedWait.Detail, repeatedWait.Detail);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Stopped, finalStatus.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.None, finalStatus.Ownership);
    }

    [Fact]
    public async Task Background_start_fails_closed_when_recovery_discovery_index_is_malformed()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopRunsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json"), "{\"schemaVersion\":2,\"revision\":1,\"entries\":[]}");

        var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var status = await runtime.ReadGovernedLoopLocalBackgroundStatusAsync();
        var stop = await runtime.StopGovernedLoopLocalBackgroundAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.RepairRequired, start.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable, start.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.Unknown, start.Ownership);
        Assert.False(start.RetryAllowed);
        Assert.Contains("human_review_recovery_requires_repair", start.Detail, StringComparison.Ordinal);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Stopped, status.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.AlreadyStopped, stop.Status);
    }

    [Fact]
    public async Task Background_restart_defers_recovery_while_a_stopped_predecessor_retains_the_fenced_lease()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var owner = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        await using var replacement = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, (await owner.StartGovernedLoopLocalBackgroundWithStatusAsync()).Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, (await owner.StopGovernedLoopLocalBackgroundAsync()).Status);
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopRunsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json"), "{\"schemaVersion\":2,\"revision\":1,\"entries\":[]}");

        var start = await replacement.StartGovernedLoopLocalBackgroundWithStatusAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.OwnedByLivePeer, start.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Degraded, start.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.LivePeer, start.Ownership);
        Assert.True(start.RetryAllowed);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.AlreadyStopped, (await replacement.StopGovernedLoopLocalBackgroundAsync()).Status);
    }

    [Fact]
    public async Task Background_start_fails_closed_when_a_current_human_review_page_contains_an_invalid_claim_item()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        const string RunId = "run-startup-human-review-invalid-claim";
        var futurePublicationTime = DateTimeOffset.UtcNow.ToUniversalTime().AddMinutes(30);
        var approved = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync(
            RunId,
            "admission-startup-human-review-invalid-claim",
            "startup-human-review-invalid-claim-loop",
            futurePublicationTime);
        var paths = new WorkspacePaths(workspace.RootPath);
        using (var seedStore = new CustomLoopRunStore(paths))
        {
            await SeedApprovedRunAsync(seedStore, approved);
        }

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

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.RepairRequired, start.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable, start.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.Unknown, start.Ownership);
        Assert.False(start.RetryAllowed);
        Assert.Contains("human_review_recovery_requires_repair", start.Detail, StringComparison.Ordinal);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Stopped, status.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.AlreadyStopped, stop.Status);
    }

    [Fact]
    public async Task Background_status_preserves_corrupt_coordinator_evidence_before_any_start()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Acquired, (await store.TryAcquireAsync(ExpiredPeerAcquisition()))!.Status);
        var ledger = Directory.EnumerateFiles(paths.AgentFile(Path.Combine("loops", "execution", "coordinator")), "ledger-*.json").Order(StringComparer.Ordinal).Last();
        await File.WriteAllTextAsync(ledger, "{invalid");

        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var status = await runtime.ReadGovernedLoopLocalBackgroundStatusAsync();
        var wait = await runtime.WaitForGovernedLoopLocalBackgroundStopAsync();
        var stop = await runtime.StopGovernedLoopLocalBackgroundAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable, status.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.Unknown, status.Ownership);
        Assert.Contains("coordinator evidence", status.Detail, StringComparison.Ordinal);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.AlreadyStopped, wait.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Unavailable, stop.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable, stop.Readiness);
    }

    [Fact]
    public async Task Background_stop_projects_a_durably_failed_coordinator_without_fabricating_success()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var started = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        Directory.CreateDirectory(paths.CustomLoopRunsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json"), "{\"schemaVersion\":2,\"revision\":1,\"entries\":[]}");
        var status = await WaitForBackgroundReadinessAsync(runtime, AgentRuntimeGovernedLoopBackgroundReadiness.Degraded);
        var stop = await runtime.StopGovernedLoopLocalBackgroundAsync();
        var repeated = await runtime.StopGovernedLoopLocalBackgroundAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, started.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Degraded, status.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.None, status.Ownership);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Failed, stop.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Degraded, stop.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.None, stop.Ownership);
        Assert.Contains("terminated fail closed", stop.Detail, StringComparison.Ordinal);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Failed, repeated.Status);
        Assert.Equal(stop.Detail, repeated.Detail);
    }
}
