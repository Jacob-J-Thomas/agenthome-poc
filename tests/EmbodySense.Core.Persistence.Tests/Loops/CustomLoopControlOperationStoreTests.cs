using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopControlOperationStoreTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 16, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Pending_and_complete_receipts_survive_restart_replay_exact_content_and_conflict_on_changed_content()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var pending = Pending("pause-operation", AuditSchema.Actors.Web);
        var first = new CustomLoopControlOperationStore(paths);

        var created = await first.BeginAsync(pending);
        var replayedPending = await new CustomLoopControlOperationStore(paths).BeginAsync(pending);
        var conflict = await new CustomLoopControlOperationStore(paths).BeginAsync(Pending(pending.OperationId, AuditSchema.Actors.Cli));
        var completed = pending with
        {
            UpdatedAtUtc = Timestamp.AddSeconds(1),
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
        Assert.Equal(CustomLoopControlOperationStoreStatus.Replayed, replayedPending.Status);
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
        await store.BeginAsync(pending);
        var failed = pending with
        {
            UpdatedAtUtc = Timestamp.AddSeconds(1),
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

    private static CustomLoopControlOperation Pending(string operationId, string actor)
    {
        var kind = CustomLoopControlKind.Pause;
        const string runId = "run-control";
        const int expectedVersion = 2;
        return new CustomLoopControlOperation(
            CustomLoopControlOperation.CurrentSchemaVersion,
            operationId,
            CustomLoopControlRequestHash.Compute(kind, runId, expectedVersion, operationId, actor),
            kind,
            runId,
            expectedVersion,
            actor,
            Timestamp,
            Timestamp,
            CustomLoopControlOperationState.Pending,
            CustomLoopControlStatus.Unknown,
            null,
            null,
            false,
            "The operation is pending.");
    }
}
