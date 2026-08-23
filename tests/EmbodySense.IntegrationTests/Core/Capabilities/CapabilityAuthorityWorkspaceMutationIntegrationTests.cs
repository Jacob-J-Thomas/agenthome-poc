using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Persistence.ToolResults;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.IntegrationTests.Core.Capabilities;

public sealed class CapabilityAuthorityWorkspaceMutationIntegrationTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Failed_skill_commit_releases_capability_authority()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new CapabilityAuthorityTransaction(paths);
        var boundary = new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, authority);

        await Assert.ThrowsAsync<IOException>(() => boundary.ExecuteAsync<bool>([Path.Combine(paths.SkillsPath, "manifest.json")], _ => Task.FromException<bool>(new IOException("failed commit"))));

        var followUp = await authority.ExecuteAsync(_ => Task.FromResult("available")).WaitAsync(_timeout);
        Assert.Equal("available", followUp);
    }

    [Fact]
    public async Task Cancelled_skill_commit_releases_capability_authority()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new CapabilityAuthorityTransaction(paths);
        var boundary = new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, authority);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boundary.ExecuteAsync<bool>([Path.Combine(paths.SkillsPath, "manifest.json")], async commitCancellationToken =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, commitCancellationToken);
            return true;
        }, cancellation.Token));

        var followUp = await authority.ExecuteAsync(_ => Task.FromResult("available")).WaitAsync(_timeout);
        Assert.Equal("available", followUp);
    }

    private static CapabilityLifecycleService CreateLifecycle(WorkspacePaths paths, ICapabilityDependentIndex dependentIndex, ICapabilityLifecycleMutationStore mutationStore, ICapabilityAuthorityTransaction authority)
    {
        return new CapabilityLifecycleService(dependentIndex, new NullCapabilityLifecycleBaselineSource(), new ThrowingCapabilityLifecycleArtifactEvidenceSource(), mutationStore, new AuditLog(paths), authority);
    }

    private static ToolBroker CreateBroker(WorkspacePaths paths, IWorkspaceMutationCommitBoundary mutationBoundary)
    {
        var policy = new PermissionPolicyStore().Load(paths);
        var executor = new LocalWorkspaceClient(paths);
        return new ToolBroker(paths, new ToolPermissionService(paths, policy), new ApprovingToolApprovalPrompt(), executor, new AuditLog(paths), LoopDefinition.CreateDefaultConversation(), new ToolResultRetentionStore(paths));
    }

    private static CapabilityLifecyclePreview CreatePreview(string operationId)
    {
        Assert.True(CapabilityId.TryParse("org.example/effect", out var capabilityId, out _));
        return new CapabilityLifecyclePreview(CapabilityLifecyclePreviewStatus.Ready, "workspace-ordering-test", operationId, CapabilityLifecycleOperationKind.Disable, capabilityId!, 1, 1, new string('a', 64), new string('b', 64), [], "ready");
    }
}
