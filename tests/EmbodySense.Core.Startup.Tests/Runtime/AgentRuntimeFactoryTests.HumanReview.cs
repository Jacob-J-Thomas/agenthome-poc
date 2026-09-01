using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task CreateAsync_composes_human_review_facade_and_keeps_catalog_non_executable_without_authority()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        Assert.NotNull(runtime.HumanReview);
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var descriptor = Assert.Single(catalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        Assert.True(descriptor.IsAdvertised);
        Assert.False(descriptor.IsExecutable);
    }

    [Fact]
    public async Task Start_background_after_server_owned_authority_and_bounded_recovery_probe_makes_human_review_executable()
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

        var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var descriptor = Assert.Single(catalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, start.Status);
        Assert.True(descriptor.IsAdvertised);
        Assert.True(descriptor.IsExecutable);

        var stop = await runtime.StopGovernedLoopLocalBackgroundAsync();
        Assert.NotEqual(AgentRuntimeGovernedLoopBackgroundStopStatus.Unavailable, stop.Status);
    }

    [Fact]
    public async Task Start_background_keeps_human_review_non_executable_when_the_empty_workspace_effect_ledger_is_corrupt()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await InitializeHumanReviewAuthorityAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.GovernedLoopEffectAttemptsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.GovernedLoopEffectAttemptsPath, "unexpected-artifact"), "corrupt");
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
