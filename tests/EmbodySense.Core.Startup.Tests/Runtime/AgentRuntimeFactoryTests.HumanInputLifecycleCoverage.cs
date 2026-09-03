using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.HumanInput.Models;
using EmbodySense.Core.Startup.Tests.HumanInput;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task Human_input_facade_resolves_remind_grant_evidence_and_maps_missing_grant_without_leaking_payload()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new HumanInputRequestStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        var create = CreateFreshHumanInputMutation(workspace.RootPath, "request-grant-coverage", "version-grant-coverage", "create-grant-coverage", HumanInputRequestStoreTestData.HashA);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(create)).Status);
        var request = Assert.IsType<HumanInputRequest>(create.RequestToAppend);
        var initialHead = Assert.IsType<HumanInputRequestLifecycleHead>(create.PrimaryHeadToWrite);
        var persistedRemind = CreateDurableLifecycleReplayMutation(
            HumanInputRequestLifecycleOperationKind.Remind,
            request,
            initialHead,
            1,
            "remind-with-missing-grant",
            null);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(persistedRemind)).Status);
        var currentHead = Assert.IsType<HumanInputRequestLifecycleHead>(persistedRemind.PrimaryHeadToWrite);
        var provider = new HumanInputRuntimeFacadeTestAuthorityProvider();
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, humanInputAuthorityProvider: provider);

        var result = await runtime.HumanInput.SubmitLifecycleAsync(new HumanInputLifecycleOperationInput(
            "remind-current-missing-grant",
            HumanInputRequestLifecycleOperationKind.Remind,
            request.RequestId,
            currentHead.LifecycleVersion,
            currentHead.Status,
            HumanInputRequestStoreTestData.Reference(request),
            null,
            "Manage one exact bounded Human Input request."));

        Assert.Equal(HumanInputOperationStatus.NotFound, result.Status);
        Assert.Null(result.Evidence);
        Assert.DoesNotContain("grant-replay", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Human_input_facade_remind_resolves_the_create_grant_reference_when_no_remind_evidence_exists()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new HumanInputRequestStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        var create = CreateFreshHumanInputMutation(workspace.RootPath, "request-ambiguous-grant", "version-ambiguous-grant", "create-ambiguous-grant", HumanInputRequestStoreTestData.HashB);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(create)).Status);
        var request = Assert.IsType<HumanInputRequest>(create.RequestToAppend);
        var head = Assert.IsType<HumanInputRequestLifecycleHead>(create.PrimaryHeadToWrite);
        var provider = new HumanInputRuntimeFacadeTestAuthorityProvider();
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, humanInputAuthorityProvider: provider);

        var result = await runtime.HumanInput.SubmitLifecycleAsync(new HumanInputLifecycleOperationInput(
            "remind-ambiguous-current-grant",
            HumanInputRequestLifecycleOperationKind.Remind,
            request.RequestId,
            head.LifecycleVersion,
            head.Status,
            HumanInputRequestStoreTestData.Reference(request),
            null,
            "Manage one exact bounded Human Input request."));

        Assert.Equal(HumanInputOperationStatus.NotFound, result.Status);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task Human_input_facade_remind_returns_conflict_before_grant_resolution_for_stale_head()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new HumanInputRequestStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        var create = CreateFreshHumanInputMutation(workspace.RootPath, "request-stale-grant", "version-stale-grant", "create-stale-grant", HumanInputRequestStoreTestData.HashC);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(create)).Status);
        var request = Assert.IsType<HumanInputRequest>(create.RequestToAppend);
        var head = Assert.IsType<HumanInputRequestLifecycleHead>(create.PrimaryHeadToWrite);
        var provider = new HumanInputRuntimeFacadeTestAuthorityProvider();
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, humanInputAuthorityProvider: provider);

        var staleVersion = await runtime.HumanInput.SubmitLifecycleAsync(new HumanInputLifecycleOperationInput(
            "remind-stale-current-grant",
            HumanInputRequestLifecycleOperationKind.Remind,
            request.RequestId,
            head.LifecycleVersion + 1,
            head.Status,
            HumanInputRequestStoreTestData.Reference(request),
            null,
            "Manage one exact bounded Human Input request."));
        var staleStatus = await runtime.HumanInput.SubmitLifecycleAsync(new HumanInputLifecycleOperationInput(
            "remind-stale-status-grant",
            HumanInputRequestLifecycleOperationKind.Remind,
            request.RequestId,
            head.LifecycleVersion,
            HumanInputRequestLifecycleStatus.Answered,
            HumanInputRequestStoreTestData.Reference(request),
            null,
            "Manage one exact bounded Human Input request."));

        Assert.Equal(HumanInputOperationStatus.Conflict, staleVersion.Status);
        Assert.Equal(HumanInputOperationStatus.Conflict, staleStatus.Status);
        Assert.Null(staleVersion.Evidence);
        Assert.Null(staleStatus.Evidence);
    }

    [Theory]
    [InlineData(AgentRuntimeHumanInputAuthorityStatus.Denied, HumanInputOperationStatus.Denied)]
    [InlineData(AgentRuntimeHumanInputAuthorityStatus.Unavailable, HumanInputOperationStatus.Unavailable)]
    public async Task Human_input_facade_maps_lifecycle_authority_posture_before_remind_resolution(
        AgentRuntimeHumanInputAuthorityStatus authorityStatus,
        HumanInputOperationStatus expectedStatus)
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new HumanInputRequestStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        var create = CreateFreshHumanInputMutation(workspace.RootPath, "request-authority-coverage", "version-authority-coverage", "create-authority-coverage", HumanInputRequestStoreTestData.HashA);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(create)).Status);
        var request = Assert.IsType<HumanInputRequest>(create.RequestToAppend);
        var head = Assert.IsType<HumanInputRequestLifecycleHead>(create.PrimaryHeadToWrite);
        var provider = new HumanInputRuntimeFacadeTestAuthorityProvider { LifecycleTermsStatus = authorityStatus };
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, humanInputAuthorityProvider: provider);

        var result = await runtime.HumanInput.SubmitLifecycleAsync(new HumanInputLifecycleOperationInput(
            "remind-authority-" + authorityStatus.ToString().ToLowerInvariant(),
            HumanInputRequestLifecycleOperationKind.Remind,
            request.RequestId,
            head.LifecycleVersion,
            head.Status,
            HumanInputRequestStoreTestData.Reference(request),
            null,
            "Manage one exact bounded Human Input request."));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Evidence);
    }
}
