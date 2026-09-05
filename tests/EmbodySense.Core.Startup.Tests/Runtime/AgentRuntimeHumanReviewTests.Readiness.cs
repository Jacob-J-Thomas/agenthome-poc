using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Tests.Capabilities;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

using static EmbodySense.Core.Startup.Tests.Runtime.AgentRuntimeFactoryTests;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeHumanReviewTests
{
    [Fact]
    public async Task Start_background_keeps_human_review_non_executable_when_authority_artifacts_are_absent()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
            new RejectingApprovalPrompt(),
            workspace.ServerStatePath,
            CreateCompatibleRuntimeStatus(executablePath))
            .WithHumanReviewDecisionAuthorizationProvider(new HumanReviewDecisionAuthorizationProviderTestDouble());

        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);

        var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var descriptor = Assert.Single(catalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, start.Status);
        Assert.False(descriptor.IsExecutable);
        Assert.NotEqual(AgentRuntimeGovernedLoopBackgroundStopStatus.Unavailable, (await runtime.StopGovernedLoopLocalBackgroundAsync()).Status);
    }

    [Fact]
    public async Task Start_background_keeps_human_review_non_executable_when_authority_artifacts_are_corrupt()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.AuthorityProfilesDocumentPath)!);
        await File.WriteAllTextAsync(paths.AuthorityProfilesDocumentPath, "{corrupt");
        await File.WriteAllTextAsync(paths.AuthorityProfilesProofPath, "{corrupt");
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
            new RejectingApprovalPrompt(),
            workspace.ServerStatePath,
            CreateCompatibleRuntimeStatus(executablePath))
            .WithHumanReviewDecisionAuthorizationProvider(new HumanReviewDecisionAuthorizationProviderTestDouble());

        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);

        await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var descriptor = Assert.Single(catalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);

        Assert.False(descriptor.IsExecutable);
        Assert.NotEqual(AgentRuntimeGovernedLoopBackgroundStopStatus.Unavailable, (await runtime.StopGovernedLoopLocalBackgroundAsync()).Status);
    }

    [Fact]
    public async Task Start_background_keeps_human_review_non_executable_when_capability_lifecycle_overlay_is_corrupt()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await InitializeHumanReviewAuthorityAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        await InitializeCapabilityLifecycleAsync(workspace, paths);
        await File.WriteAllTextAsync(paths.CapabilityLifecycleDocumentPath, "{corrupt");
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
            new RejectingApprovalPrompt(),
            workspace.ServerStatePath,
            CreateCompatibleRuntimeStatus(executablePath))
            .WithHumanReviewDecisionAuthorizationProvider(new HumanReviewDecisionAuthorizationProviderTestDouble());

        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);

        await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();

        Assert.Equal("unavailable", catalog.Status);
        Assert.DoesNotContain(catalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview && item.IsExecutable);
        Assert.NotEqual(AgentRuntimeGovernedLoopBackgroundStopStatus.Unavailable, (await runtime.StopGovernedLoopLocalBackgroundAsync()).Status);
    }

    [Fact]
    public async Task Start_background_makes_human_review_executable_only_with_initialized_authority_artifacts()
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

        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);

        var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var descriptor = Assert.Single(catalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, start.Status);
        Assert.True(descriptor.IsExecutable);
        Assert.NotEqual(AgentRuntimeGovernedLoopBackgroundStopStatus.Unavailable, (await runtime.StopGovernedLoopLocalBackgroundAsync()).Status);
    }

    [Fact]
    public async Task Confirmed_background_stop_revokes_human_review_graph_execution_without_restricting_the_manual_facade()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await InitializeHumanReviewAuthorityAsync(workspace);
        var blueprint = await CreateLivePendingBlueprintAsync("run-human-review-readiness-stop");
        await PersistPendingHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                new RejectingApprovalPrompt(),
                workspace.ServerStatePath,
                CreateCompatibleRuntimeStatus(executablePath))
            .WithHumanReviewDecisionAuthorizationProvider(new HumanReviewDecisionAuthorizationProviderTestDouble());

        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);

        var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var runningCatalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var manualBeforeStop = await runtime.HumanReview.ReadAsync(blueprint.Id);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync(cancellation.Token));
        var stop = await runtime.StopGovernedLoopLocalBackgroundAsync();
        var stoppedCatalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var manualAfterStop = await runtime.HumanReview.ReadAsync(blueprint.Id);

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, start.Status);
        Assert.True(Assert.Single(runningCatalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview).IsExecutable);
        Assert.Equal(HumanReviewReadStatus.Ready, manualBeforeStop.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, stop.Status);
        Assert.False(Assert.Single(stoppedCatalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview).IsExecutable);
        Assert.Equal(HumanReviewReadStatus.Ready, manualAfterStop.Status);
    }

    [Fact]
    public async Task Healthy_restart_revalidates_human_review_dependencies_before_graph_execution()
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
        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);

        var initialStart = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var initialCatalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var repeatedStart = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var repeatedCatalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, (await runtime.StopGovernedLoopLocalBackgroundAsync()).Status);
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.GovernedLoopRevisionsPath);
        var lifecyclePath = Path.Combine(paths.GovernedLoopRevisionsPath, "lifecycle.json");
        await File.WriteAllTextAsync(lifecyclePath, "{\"corrupt\":true");

        var degradedStart = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var degradedCatalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, (await runtime.StopGovernedLoopLocalBackgroundAsync()).Status);
        File.Delete(lifecyclePath);
        var repairedStart = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var repairedCatalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, initialStart.Status);
        Assert.True(HumanReviewDescriptor(initialCatalog).IsExecutable);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.AlreadyRunning, repeatedStart.Status);
        Assert.True(HumanReviewDescriptor(repeatedCatalog).IsExecutable);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, degradedStart.Status);
        Assert.False(HumanReviewDescriptor(degradedCatalog).IsExecutable);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, repairedStart.Status);
        Assert.True(HumanReviewDescriptor(repairedCatalog).IsExecutable);
    }

    [Fact]
    public async Task Coordinator_heartbeat_corruption_revokes_human_review_graph_execution_outside_a_review_pass()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await InitializeHumanReviewAuthorityAsync(workspace);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var observer = new SignalingCoordinatorBoundaryObserver();
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                new RejectingApprovalPrompt(),
                workspace.ServerStatePath,
                CreateCompatibleRuntimeStatus(executablePath))
            .WithHumanReviewDecisionAuthorizationProvider(new HumanReviewDecisionAuthorizationProviderTestDouble())
            .WithGovernedLoopLocalCoordinatorBoundaryObserver(observer);
        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);

        var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var runningCatalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        observer.HoldHeartbeat();
        try
        {
            await observer.HeldHeartbeatDue.WaitAsync(TimeSpan.FromSeconds(5));
            var coordinatorPath = new WorkspacePaths(workspace.RootPath).AgentFile(Path.Combine("loops", "execution", "coordinator"));
            var ledgerPath = Assert.Single(Directory.EnumerateFiles(coordinatorPath, "ledger-*.json"));
            await File.WriteAllTextAsync(ledgerPath, "{\"corrupt\":true");
        }
        finally
        {
            observer.ReleaseHeartbeat();
        }

        var unavailable = await WaitForBackgroundReadinessAsync(runtime, AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable);
        var unavailableCatalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, start.Status);
        Assert.True(HumanReviewDescriptor(runningCatalog).IsExecutable);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.Unknown, unavailable.Ownership);
        Assert.False(HumanReviewDescriptor(unavailableCatalog).IsExecutable);
    }

    [Fact]
    public async Task Live_peer_ownership_does_not_promote_this_runtimes_human_review_graph_execution()
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
        await using var owner = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);
        await using var peer = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);

        var ownerStart = await owner.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var peerStart = await peer.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var ownerCatalog = await owner.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var peerCatalog = await peer.GovernedLoopGraphAuthoring.ReadCatalogAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, ownerStart.Status);
        Assert.True(HumanReviewDescriptor(ownerCatalog).IsExecutable);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.OwnedByLivePeer, peerStart.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Degraded, peerStart.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.LivePeer, peerStart.Ownership);
        Assert.False(HumanReviewDescriptor(peerCatalog).IsExecutable);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, (await owner.StopGovernedLoopLocalBackgroundAsync()).Status);
    }

    private static async Task InitializeHumanReviewAuthorityAsync(TestWorkspace workspace)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var profileStore = new AuthorityProfileStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        var profileMutation = await profileStore.MutateAsync(new AuthorityProfileMutation(
            AuthorityProfileMutationKind.Create,
            "initialize-human-review-readiness-authority",
            0,
            AuthorityGrantApplicationTestFixture.Profile(),
            null,
            null,
            AuthorityGrantApplicationTestFixture.Actor(),
            AuthorityGrantApplicationTestFixture.Purpose()));
        Assert.Equal(AuthorityProfileMutationStatus.Applied, profileMutation.Status);
    }

    private static async Task InitializeCapabilityLifecycleAsync(TestWorkspace workspace, WorkspacePaths paths)
    {
        var stage = CapabilityAdmissionLifecycleTestData.Stage();
        var catalogTrust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var catalogService = new CapabilityCatalogService(new CapabilityCatalogStore(paths, catalogTrust));
        var revision = (await catalogService.ReadAsync(null, 1)).Page!.CatalogRevision;
        revision = (await catalogService.DeclareAsync(stage.Manifest.Descriptor, revision, "declare-human-review-readiness")).CatalogRevision!.Value;
        var artifactTrust = new FileCapabilityArtifactStateTrustProvider(workspace.ServerStatePath);
        var verifier = new AlwaysTrustedLifecycleArtifactVerifier();
        var artifactStore = new CapabilityArtifactStore(paths, artifactTrust, verifier);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await artifactStore.StageAsync(stage)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await artifactStore.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate-human-review-readiness"))).Status);
        var lifecycle = CapabilityLifecycleFactory.Create(paths, catalogTrust, artifactTrust, verifier, new AuditLog(paths));
        var preview = await lifecycle.PreviewAsync(new CapabilityLifecyclePreviewRequest(
            "disable-human-review-readiness",
            CapabilityLifecycleOperationKind.Disable,
            stage.Manifest.Descriptor.Id));
        Assert.Equal(CapabilityLifecyclePreviewStatus.Ready, preview.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await lifecycle.MutateAsync(preview)).Status);
    }

    private static GovernedLoopGraphCatalogNodeSnapshot HumanReviewDescriptor(GovernedLoopGraphCatalogResponse catalog)
        => Assert.Single(catalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);

    [Fact]
    public async Task Start_background_keeps_human_review_non_executable_when_governed_graph_revision_store_is_corrupt()
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

        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.GovernedLoopRevisionsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.GovernedLoopRevisionsPath, "lifecycle.json"), "{\"corrupt\":true");

        var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var descriptor = Assert.Single(catalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, start.Status);
        Assert.True(descriptor.IsAdvertised);
        Assert.False(descriptor.IsExecutable);

        var stop = await runtime.StopGovernedLoopLocalBackgroundAsync();
        Assert.NotEqual(AgentRuntimeGovernedLoopBackgroundStopStatus.Unavailable, stop.Status);
    }
}
