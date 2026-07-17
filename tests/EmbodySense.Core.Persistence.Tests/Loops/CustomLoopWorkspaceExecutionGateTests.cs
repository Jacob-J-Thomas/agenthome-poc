using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopWorkspaceExecutionGateTests
{
    private static readonly string FirstHash = new('1', CustomLoopLimits.Sha256HexCharacters);
    private static readonly string SecondHash = new('2', CustomLoopLimits.Sha256HexCharacters);

    [Fact]
    public async Task Canonical_workspace_gate_never_waits_and_releases_after_execution()
    {
        using var workspace = new TestWorkspace();
        var firstPaths = new WorkspacePaths(workspace.RootPath);
        var canonicalAlias = new WorkspacePaths(Path.Combine(workspace.RootPath, "."));
        await using var first = new CustomLoopWorkspaceExecutionGate(firstPaths);
        await using var second = new CustomLoopWorkspaceExecutionGate(canonicalAlias);

        var acquired = first.TryAcquire("invoke-one", FirstHash);
        var workspaceBusy = second.TryAcquire("invoke-two", SecondHash);
        var sameOperation = second.TryAcquire("invoke-one", FirstHash);
        var changedOperation = second.TryAcquire("invoke-one", SecondHash);

        Assert.Equal(CustomLoopExecutionLeaseStatus.Acquired, acquired.Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.WorkspaceBusy, workspaceBusy.Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationInProgress, sameOperation.Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationConflict, changedOperation.Status);

        acquired.Lease!.Dispose();
        acquired.Lease.Dispose();
        using var next = second.TryAcquire("invoke-two", SecondHash).Lease;
        Assert.NotNull(next);
        Assert.Throws<ArgumentException>(() => first.TryAcquire("INVALID", FirstHash));
        Assert.Throws<ArgumentException>(() => first.TryAcquire("invoke-three", "bad-hash"));
    }

    [Fact]
    public async Task Gate_holds_file_ownership_until_all_host_references_are_disposed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = new CustomLoopWorkspaceExecutionGate(paths);
        var second = new CustomLoopWorkspaceExecutionGate(paths);

        Assert.True(File.Exists(paths.CustomLoopHostLockPath));
        Assert.Throws<IOException>(() => new FileStream(paths.CustomLoopHostLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read));

        await first.DisposeAsync();
        Assert.Throws<IOException>(() => new FileStream(paths.CustomLoopHostLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read));
        await second.DisposeAsync();
        await second.DisposeAsync();

        using var ownershipAfterRelease = new FileStream(paths.CustomLoopHostLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Assert.True(ownershipAfterRelease.CanWrite);
    }
}
