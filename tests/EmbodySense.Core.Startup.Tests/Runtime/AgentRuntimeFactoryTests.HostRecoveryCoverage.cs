using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task Background_start_projects_human_review_recovery_unavailable_when_discovery_index_cannot_be_read()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var paths = new WorkspacePaths(workspace.RootPath);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var linkedIndexTarget = Path.Combine(workspace.RootPath, "human-review-index-target.json");
        Directory.CreateDirectory(paths.CustomLoopRunsPath);
        await File.WriteAllTextAsync(linkedIndexTarget, "{}");
        File.CreateSymbolicLink(indexPath, linkedIndexTarget);

        var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Unavailable, start.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable, start.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.Unknown, start.Ownership);
        Assert.True(start.RetryAllowed);
        Assert.Equal("governed_human_review_recovery_unavailable: bounded recovery dependencies are unavailable.", start.Detail);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.AlreadyStopped, (await runtime.StopGovernedLoopLocalBackgroundAsync()).Status);
    }
}
