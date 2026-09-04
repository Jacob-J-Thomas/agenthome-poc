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
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Tests.Capabilities;
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
