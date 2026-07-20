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

        var busyReservation = second.TryReserveWorkspaceBusyOutcome("invoke-two", SecondHash);
        Assert.Equal(CustomLoopExecutionLeaseStatus.BusyOutcomeReserved, busyReservation.Status);
        Assert.NotNull(busyReservation.Lease);
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationInProgress, first.TryReserveWorkspaceBusyOutcome("invoke-two", SecondHash).Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationConflict, first.TryReserveWorkspaceBusyOutcome("invoke-two", FirstHash).Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationInProgress, second.TryReserveWorkspaceBusyOutcome("invoke-one", FirstHash).Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationConflict, second.TryReserveWorkspaceBusyOutcome("invoke-one", SecondHash).Status);
        acquired.Lease!.Dispose();
        acquired.Lease.Dispose();
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationInProgress, first.TryAcquire("invoke-two", SecondHash).Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationConflict, first.TryAcquire("invoke-two", FirstHash).Status);
        busyReservation.Lease!.Dispose();
        busyReservation.Lease.Dispose();
        using var next = second.TryAcquire("invoke-two", SecondHash).Lease;
        Assert.NotNull(next);
        next.Dispose();
        Assert.Equal(CustomLoopExecutionLeaseStatus.WorkspaceAvailable, second.TryReserveWorkspaceBusyOutcome("invoke-three", FirstHash).Status);
        Assert.Throws<ArgumentException>(() => first.TryAcquire("INVALID", FirstHash));
        Assert.Throws<ArgumentException>(() => first.TryAcquire("invoke-three", "bad-hash"));
        Assert.Throws<ArgumentException>(() => first.TryReserveWorkspaceBusyOutcome("INVALID", FirstHash));
        Assert.Throws<ArgumentException>(() => first.TryReserveWorkspaceBusyOutcome("invoke-three", "bad-hash"));
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

    [Fact]
    public async Task Gate_reports_unavailable_host_without_blocking_construction_when_another_process_owns_the_lock()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.LoopRunsPath);
        using var ownership = new FileStream(paths.CustomLoopHostLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        await using var gate = new CustomLoopWorkspaceExecutionGate(paths);

        Assert.Equal(CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable, gate.TryAcquire("invoke-one", FirstHash).Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable, gate.TryReserveWorkspaceBusyOutcome("invoke-one", FirstHash).Status);
    }

    [Fact]
    public void Gate_rejects_a_reparse_point_run_root_when_the_platform_allows_links()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LoopRunsPath)!);
        var target = workspace.File("reparse-run-target");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(paths.LoopRunsPath, target);
        }
        catch (Exception linkException) when (linkException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var exception = Assert.Throws<InvalidOperationException>(() => new CustomLoopWorkspaceExecutionGate(paths));
        Assert.Contains("reparse points or junctions", exception.Message, StringComparison.Ordinal);
    }
}
