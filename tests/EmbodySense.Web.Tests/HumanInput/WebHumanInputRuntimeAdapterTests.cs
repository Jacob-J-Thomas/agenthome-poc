using System.Text.Json;
using EmbodySense.Core.Startup.HumanInput.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web.Tests;

[Collection(EphemeralPortApiCollection.Name)]
public sealed class WebHumanInputRuntimeAdapterTests
{
    [Fact]
    public async Task Adapter_delegates_all_human_input_operations_through_one_retained_host()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var approvals = new WebApprovalCoordinator();
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath]);
        await using var host = new WebAgentRuntimeHost(
            options,
            approvals,
            WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath),
            null,
            runtimeStatus => AgentRuntimeFactory.ForFileCapabilityTrustRoot(approvals, workspace.ServerStatePath, runtimeStatus));
        await host.InitializeWorkspaceAsync();
        var adapter = new WebHumanInputRuntimeAdapter(host);

        var page = await adapter.ListAsync(new HumanInputRequestPosturePageRequest(5));
        var read = await adapter.ReadAsync("missing-request");
        var lifecycle = await adapter.SubmitLifecycleAsync(new HumanInputSurfaceLifecycleOperationInput("operation-1", "Reject", "missing-request", 1, "Pending", null, null, "reason"));
        var response = await adapter.SubmitResponseAsync(new HumanInputSurfaceResponseOperationInput("operation-2", "Submit", "missing-request", 1, "Pending", null, "response-1", JsonDocument.Parse("null").RootElement.Clone(), null));
        var preparation = await adapter.PrepareSupersedeAsync(new HumanInputSupersedePreparationInput("operation-3", "missing-request", null, 1, "Pending", "purpose", "prompt", JsonDocument.Parse("{}").RootElement.Clone(), "Public", DateTimeOffset.UtcNow.AddMinutes(1), JsonDocument.Parse("{}").RootElement.Clone()));

        Assert.Equal(HumanInputRequestPosturePageStatus.Ready, page.Status);
        Assert.Equal(HumanInputRequestPostureReadStatus.NotFound, read.Status);
        Assert.Equal(HumanInputOperationStatus.Unavailable, lifecycle.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, response.Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, preparation.Status);
    }
}
