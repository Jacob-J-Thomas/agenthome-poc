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

    [Theory]
    [InlineData(ToolCommand.Append)]
    [InlineData(ToolCommand.Write)]
    [InlineData(ToolCommand.Delete)]
    public async Task Every_local_skill_mutation_routes_through_capability_authority(ToolCommand command)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new CapabilityAuthorityTransaction(paths);
        var signalingAuthority = new SignalingCapabilityAuthorityTransaction(authority);
        var boundary = new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, signalingAuthority);
        var client = new LocalWorkspaceClient(paths, boundary);
        var skillPath = Path.Combine(paths.SkillsPath, "route", "capability-dependencies.json");
        Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
        await File.WriteAllTextAsync(skillPath, "initial");

        _ = command switch
        {
            ToolCommand.Append => await client.AppendAsync(skillPath, " appended"),
            ToolCommand.Write => await client.WriteAsync(skillPath, "replacement"),
            ToolCommand.Delete => await client.DeleteAsync(skillPath),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Only workspace mutation commands are covered by this test.")
        };

        await signalingAuthority.ExecutionAttempted.WaitAsync(_timeout);
        Assert.Equal(command == ToolCommand.Delete, !File.Exists(skillPath));
    }

    [Fact]
    public async Task Approved_skill_write_cannot_commit_after_final_capture_before_lifecycle_commit()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new CapabilityAuthorityTransaction(paths);
        var dependentIndex = new FixedCapabilityDependentIndex();
        var mutationStore = new BlockingCapabilityLifecycleMutationStore();
        var lifecycle = CreateLifecycle(paths, dependentIndex, mutationStore, authority);
        var lifecycleTask = lifecycle.MutateAsync(CreatePreview("lifecycle-first"));
        await mutationStore.MutationEntered.WaitAsync(_timeout);
        Assert.Equal(2, dependentIndex.CaptureCount);
        var skillPath = Path.Combine(paths.SkillsPath, "race", "capability-dependencies.json");
        var writerAuthority = new SignalingCapabilityAuthorityTransaction(authority);
        var broker = CreateBroker(paths, new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, writerAuthority));

        var writeTask = broker.ExecuteAsync(new ToolRequest(ToolCommand.Write, Path.GetRelativePath(paths.RootPath, skillPath), "{}"));
        await writerAuthority.ExecutionAttempted.WaitAsync(_timeout);

        Assert.False(writeTask.IsCompleted);
        Assert.False(File.Exists(skillPath));
        mutationStore.ReleaseMutation();
        var lifecycleResult = await lifecycleTask.WaitAsync(_timeout);
        var writeResult = await writeTask.WaitAsync(_timeout);

        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, lifecycleResult.Status);
        Assert.True(writeResult.Succeeded);
        Assert.Equal("{}", await File.ReadAllTextAsync(skillPath));
    }

    [Fact]
    public async Task Lifecycle_capture_cannot_start_while_an_approved_skill_commit_holds_authority()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new CapabilityAuthorityTransaction(paths);
        var productionBoundary = new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, authority);
        var blockingBoundary = new BlockingWorkspaceMutationCommitBoundary(productionBoundary);
        var broker = CreateBroker(paths, blockingBoundary);
        var skillPath = Path.Combine(paths.SkillsPath, "race", "capability-dependencies.json");
        var writeTask = broker.ExecuteAsync(new ToolRequest(ToolCommand.Write, Path.GetRelativePath(paths.RootPath, skillPath), "{}"));
        await blockingBoundary.CommitEntered.WaitAsync(_timeout);
        var dependentIndex = new FixedCapabilityDependentIndex();
        var mutationStore = new BlockingCapabilityLifecycleMutationStore(initiallyReleased: true);
        var lifecycleAuthority = new SignalingCapabilityAuthorityTransaction(authority);
        var lifecycle = CreateLifecycle(paths, dependentIndex, mutationStore, lifecycleAuthority);

        var lifecycleTask = lifecycle.MutateAsync(CreatePreview("skill-first"));
        await lifecycleAuthority.ExecutionAttempted.WaitAsync(_timeout);

        Assert.False(dependentIndex.CaptureEntered.IsCompleted);
        Assert.False(File.Exists(skillPath));
        blockingBoundary.ReleaseCommit();
        var writeResult = await writeTask.WaitAsync(_timeout);
        var lifecycleResult = await lifecycleTask.WaitAsync(_timeout);

        Assert.True(writeResult.Succeeded);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, lifecycleResult.Status);
        Assert.Equal(2, dependentIndex.CaptureCount);
    }

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
        var executor = new LocalWorkspaceClient(paths, mutationBoundary);
        return new ToolBroker(paths, new ToolPermissionService(paths, policy), new ApprovingToolApprovalPrompt(), executor, new AuditLog(paths), LoopDefinition.CreateDefaultConversation(), new ToolResultRetentionStore(paths));
    }

    private static CapabilityLifecyclePreview CreatePreview(string operationId)
    {
        Assert.True(CapabilityId.TryParse("org.example/effect", out var capabilityId, out _));
        return new CapabilityLifecyclePreview(CapabilityLifecyclePreviewStatus.Ready, "workspace-ordering-test", operationId, CapabilityLifecycleOperationKind.Disable, capabilityId!, 1, 1, new string('a', 64), new string('b', 64), [], "ready");
    }
}
