using EmbodySense.Core.Application.Loops.Models;
using System.Diagnostics;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopControlOperationStoreTests
{
    private static readonly DateTimeOffset _timestamp = new(2026, 7, 16, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Pending_and_complete_receipts_survive_restart_replay_exact_content_and_conflict_on_changed_content()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var pending = Pending("pause-operation", AuditSchema.Actors.Web);
        var first = new CustomLoopControlOperationStore(paths);

        var created = await first.BeginAsync(pending);
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var replayedPending = await new CustomLoopControlOperationStore(paths).BeginAsync(pending);
        var conflict = await new CustomLoopControlOperationStore(paths).BeginAsync(Pending(pending.OperationId, AuditSchema.Actors.Cli));
        var completed = created.Operation! with
        {
            UpdatedAtUtc = created.Operation.UpdatedAtUtc.AddSeconds(1),
            State = CustomLoopControlOperationState.Complete,
            Outcome = CustomLoopControlStatus.PauseRequested,
            ResultLifecycleVersion = 3,
            ResultRunStatus = CustomLoopRunStatus.PauseRequested,
            OutcomeAuditRecorded = true,
            Detail = "Pause was durably requested."
        };
        var completion = await first.CompleteAsync(completed);
        var restarted = new CustomLoopControlOperationStore(paths);
        var loaded = await restarted.GetAsync(pending.OperationId);
        var replayedComplete = await restarted.BeginAsync(pending);

        Assert.Equal(CustomLoopControlOperationStoreStatus.Created, created.Status);
        Assert.Equal(CustomLoopControlOperationStoreStatus.OwnershipUnproven, replayedPending.Status);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Conflict, conflict.Status);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, completion.Status);
        Assert.Equal(completed, loaded);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Replayed, replayedComplete.Status);
        Assert.Equal(CustomLoopControlOperationState.Complete, replayedComplete.Operation!.State);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, pending.OperationId + ".json")));
        Assert.Empty(Directory.EnumerateFiles(paths.CustomLoopControlOperationsPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Failed_receipt_without_a_run_snapshot_is_persisted_and_replayed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var pending = Pending("load-failure", AuditSchema.Actors.Web);
        var store = new CustomLoopControlOperationStore(paths);
        var created = await store.BeginAsync(pending);
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var failed = created.Operation! with
        {
            UpdatedAtUtc = created.Operation.UpdatedAtUtc.AddSeconds(1),
            State = CustomLoopControlOperationState.Complete,
            Outcome = CustomLoopControlStatus.Failed,
            Detail = "The run could not be loaded safely."
        };

        var completion = await store.CompleteAsync(failed);
        var replay = await new CustomLoopControlOperationStore(paths).BeginAsync(pending);

        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, completion.Status);
        Assert.Equal(failed, completion.Operation);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Replayed, replay.Status);
        Assert.Equal(failed, replay.Operation);
    }

    [Fact]
    public async Task Pending_receipt_is_reowned_only_after_the_previous_execution_lease_is_released()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var pending = Pending("pause-orphan-recovery", AuditSchema.Actors.Web);
        var first = await new CustomLoopControlOperationStore(paths).BeginAsync(pending);
        var firstLease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(first.Lease);
        var liveRetry = await new CustomLoopControlOperationStore(paths).BeginAsync(pending);

        Assert.Equal(CustomLoopControlOperationStoreStatus.OwnershipUnproven, liveRetry.Status);
        Assert.Null(liveRetry.Lease);

        firstLease.Dispose();
        var recovered = await new CustomLoopControlOperationStore(paths).BeginAsync(pending);
        using var recoveredLease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(recovered.Lease);

        Assert.Equal(CustomLoopControlOperationStoreStatus.Replayed, recovered.Status);
        Assert.NotEqual(first.Operation!.OwnerGenerationId, recovered.Operation!.OwnerGenerationId);
        Assert.Equal(recoveredLease.OwnerGenerationId, recovered.Operation.OwnerGenerationId);
        Assert.Equal(Environment.ProcessId, recovered.Operation.OwnerProcessId);
        Assert.Contains("orphaned", recovered.Operation.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Concurrent_same_process_retries_allow_only_one_replacement_owner()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var pending = Pending("pause-concurrent-orphan-recovery", AuditSchema.Actors.Web);
        var first = await new CustomLoopControlOperationStore(paths).BeginAsync(pending);
        Assert.NotNull(first.Lease);
        first.Lease.Dispose();

        var retries = await Task.WhenAll(
            new CustomLoopControlOperationStore(paths).BeginAsync(pending),
            new CustomLoopControlOperationStore(paths).BeginAsync(pending));
        var recovered = Assert.Single(retries, result => result.Status == CustomLoopControlOperationStoreStatus.Replayed);
        var blocked = Assert.Single(retries, result => result.Status == CustomLoopControlOperationStoreStatus.OwnershipUnproven);
        using var recoveredLease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(recovered.Lease);

        Assert.Null(blocked.Lease);
        Assert.Equal(recoveredLease.OwnerGenerationId, recovered.Operation!.OwnerGenerationId);
    }

    [Theory]
    [InlineData(CustomLoopControlKind.Pause)]
    [InlineData(CustomLoopControlKind.Cancel)]
    [InlineData(CustomLoopControlKind.Resume)]
    public async Task Process_exit_proves_a_pre_transition_receipt_is_orphaned_before_explicit_retry_reowns_it(CustomLoopControlKind kind)
    {
        using var workspace = new TestWorkspace();
        var operationId = $"{kind.ToString().ToLowerInvariant()}-crashed-owner";
        var pending = Pending(operationId, AuditSchema.Actors.Web) with { Kind = kind };
        pending = pending with { RequestHash = CustomLoopControlRequestHash.Compute(kind, pending.RunId, pending.ExpectedLifecycleVersion, operationId, pending.Actor) };
        using var process = StartControlOperationHost(workspace.RootPath, pending);
        try
        {
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var liveRetry = await new CustomLoopControlOperationStore(new WorkspacePaths(workspace.RootPath)).BeginAsync(pending);

            Assert.Equal(CustomLoopControlOperationStoreStatus.OwnershipUnproven, liveRetry.Status);
            Assert.Equal(process.Id, liveRetry.Operation!.OwnerProcessId);
            Assert.Null(liveRetry.Lease);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            var recovered = await new CustomLoopControlOperationStore(new WorkspacePaths(workspace.RootPath)).BeginAsync(pending);
            using var recoveredLease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(recovered.Lease);

            Assert.Equal(CustomLoopControlOperationStoreStatus.Replayed, recovered.Status);
            Assert.Equal(Environment.ProcessId, recovered.Operation!.OwnerProcessId);
            Assert.Equal(recoveredLease.OwnerGenerationId, recovered.Operation.OwnerGenerationId);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Persisted_json_depth_failure_is_distinct_from_malformed_json()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopControlOperationsPath);
        var path = Path.Combine(paths.CustomLoopControlOperationsPath, "depth-operation.json");
        await File.WriteAllTextAsync(path, NestedJson(33));
        var store = new CustomLoopControlOperationStore(paths);

        var depth = await Assert.ThrowsAsync<FormatException>(() => store.GetAsync("depth-operation"));

        Assert.Contains(path, depth.Message, StringComparison.Ordinal);
        Assert.Contains("maximum persisted JSON nesting depth of 32", depth.Message, StringComparison.Ordinal);
        Assert.Contains("not a loop-iteration, traversal, or run-duration limit", depth.Message, StringComparison.Ordinal);
        Assert.Contains("remove the malformed pre-1.0 artifact", depth.Message, StringComparison.Ordinal);

        await File.WriteAllTextAsync(path, "{invalid");
        var malformed = await Assert.ThrowsAsync<FormatException>(() => store.GetAsync("depth-operation"));
        Assert.Contains("contains invalid JSON or UTF-8", malformed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nesting depth", malformed.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("D800")]
    [InlineData("DC00")]
    public async Task Persisted_control_operation_with_malformed_actor_fails_through_canonical_format_validation(string codeUnit)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var pending = Pending("malformed-control-actor", AuditSchema.Actors.Web);
        var store = new CustomLoopControlOperationStore(paths);

        Assert.Equal(CustomLoopControlOperationStoreStatus.Created, (await store.BeginAsync(pending)).Status);
        var path = Path.Combine(paths.CustomLoopControlOperationsPath, pending.OperationId + ".json");
        var persisted = await File.ReadAllTextAsync(path);
        var malformed = persisted.Replace(AuditSchema.Actors.Web, "\\u" + codeUnit, StringComparison.Ordinal);
        Assert.NotEqual(persisted, malformed);
        await File.WriteAllTextAsync(path, malformed);

        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(pending.OperationId));
    }

    private static CustomLoopControlOperation Pending(string operationId, string actor)
    {
        var kind = CustomLoopControlKind.Pause;
        const string RunId = "run-control";
        const int ExpectedVersion = 2;
        return new CustomLoopControlOperation(
            CustomLoopControlOperation.CurrentSchemaVersion,
            operationId,
            CustomLoopControlRequestHash.Compute(kind, RunId, ExpectedVersion, operationId, actor),
            kind,
            RunId,
            ExpectedVersion,
            actor,
            _timestamp,
            _timestamp,
            CustomLoopControlOperationState.Pending,
            CustomLoopControlStatus.Unknown,
            null,
            null,
            false,
            "The operation is pending.");
    }

    private static string NestedJson(int depth) => string.Concat(Enumerable.Repeat("{\"nested\":", depth)) + "null" + new string('}', depth);

    private static Process StartControlOperationHost(string workspaceRoot, CustomLoopControlOperation pending)
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = outputDirectory.Name;
        var configuration = outputDirectory.Parent?.Name ?? throw new DirectoryNotFoundException("The active test build configuration could not be resolved.");
        var hostAssembly = Path.Combine(FindRepositoryRoot(), "tests", "EmbodySense.CancellationHost", "bin", configuration, targetFramework, "EmbodySense.CancellationHost.dll");
        Assert.True(File.Exists(hostAssembly), $"Control-operation host assembly was not built at `{hostAssembly}`.");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(hostAssembly);
        startInfo.ArgumentList.Add("hold-control");
        startInfo.ArgumentList.Add(workspaceRoot);
        startInfo.ArgumentList.Add(pending.Kind.ToString());
        startInfo.ArgumentList.Add(pending.RunId);
        startInfo.ArgumentList.Add(pending.ExpectedLifecycleVersion.ToString());
        startInfo.ArgumentList.Add(pending.OperationId);
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The control-operation owner process could not be started.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbodySense.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root could not be located from the test output directory.");
    }
}
