using EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Tests.Support;
using EmbodySense.Web;
using EmbodySense.Web.Services;

namespace EmbodySense.Web.Tests;

[Collection(EphemeralPortApiCollection.Name)]
public sealed class WebAgentRuntimeHostEffectReconciliationTests
{
    [Fact]
    public async Task List_wrapper_enters_the_retained_runtime_gate_and_returns_the_canonical_page()
    {
        using var workspace = new TestWorkspace();
        var executablePath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", executablePath]);
        await using var host = new WebAgentRuntimeHost(options, new WebApprovalCoordinator(), CompatibleStatus(executablePath));
        await host.InitializeWorkspaceAsync();

        var page = await host.ListAsync(new GovernedLoopEffectReconciliationPageRequest(1));

        Assert.Equal(GovernedLoopEffectReconciliationPageStatus.Ready, page.Status);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Effect_reconciliation_wrappers_require_an_initialized_workspace_before_runtime_acquisition()
    {
        using var workspace = new TestWorkspace();
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test"]);
        await using var host = new WebAgentRuntimeHost(options, new WebApprovalCoordinator());
        var reference = Reference();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ListAsync(new GovernedLoopEffectReconciliationPageRequest(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReadAsync(reference));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ListProbeContractsAsync(new GovernedLoopEffectReconciliationPageRequest(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ProbeAsync("probe-one", reference));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.AssessAsync("assess-one", reference));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ApplyDispositionAsync("dispose-one", reference, GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReadResolutionAsync(reference));
    }

    [Fact]
    public async Task Effect_reconciliation_gate_rejects_new_work_after_the_host_reaches_its_disposal_boundary()
    {
        using var workspace = new TestWorkspace();
        var executablePath = workspace.File("not-used-by-post-disposal-gate-test.cmd");
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", executablePath]);
        await using var host = new WebAgentRuntimeHost(options, new WebApprovalCoordinator(), CompatibleStatus(executablePath));
        await host.InitializeWorkspaceAsync();

        await host.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.ListAsync(new GovernedLoopEffectReconciliationPageRequest(1)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.ReadResolutionAsync(Reference()));
    }

    private static GovernedLoopEffectReconciliationCaseReference Reference()
        => new("case-one", 3, Hash('a'), Hash('b'));

    private static CodexRuntimeStatus CompatibleStatus(string executablePath)
        => new(
            CodexRuntimeCompatibility.Compatible,
            executablePath,
            Path.GetFullPath(executablePath),
            "codex-cli 999.0.0-test",
            "gpt-test",
            "controlled test",
            "The isolated fake provider is pre-admitted for this host gate test.");

    private static string Hash(char value) => new(value, 64);
}
