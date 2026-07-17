using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopInvocationOperationStoreTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 16, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Invocation_receipt_replays_exact_busy_outcome_across_restart_and_conflicts_on_changed_content()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var pending = Pending("invoke-operation", "first prompt");
        var first = new CustomLoopInvocationOperationStore(paths);

        var created = await first.BeginAsync(pending);
        var replayedPending = await new CustomLoopInvocationOperationStore(paths).BeginAsync(pending);
        var conflict = await new CustomLoopInvocationOperationStore(paths).BeginAsync(Pending(pending.OperationId, "changed prompt"));
        var completed = pending with
        {
            UpdatedAtUtc = Timestamp.AddSeconds(1),
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = CustomLoopInvocationOutcome.WorkspaceExecutionBusy,
            AdmissionStatus = "WorkspaceExecutionBusy",
            Detail = "workspace_execution_busy: no run was created."
        };
        var completion = await first.CompleteAsync(completed);
        var exactCompletionReplay = await first.CompleteAsync(completed);
        var changedCompletion = await first.CompleteAsync(completed with { Detail = "changed durable outcome" });
        var restarted = new CustomLoopInvocationOperationStore(paths);
        var loaded = await restarted.GetAsync(pending.OperationId);
        var replayedComplete = await restarted.BeginAsync(pending);

        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, created.Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Replayed, replayedPending.Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Conflict, conflict.Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Completed, completion.Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Replayed, exactCompletionReplay.Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Conflict, changedCompletion.Status);
        Assert.Equal(completed, loaded);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Replayed, replayedComplete.Status);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopInvocationOperationsPath, pending.OperationId + ".json")));
        Assert.Empty(Directory.EnumerateFiles(paths.CustomLoopInvocationOperationsPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Concurrent_same_operation_has_one_creator_and_validation_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopInvocationOperationStore(paths);
        var pending = Pending("invoke-concurrent", "prompt");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.BeginAsync(pending)));
        var missingCompletion = await store.CompleteAsync(CompletedAdmitted(Pending("invoke-missing", "prompt")));

        Assert.Single(outcomes, item => item.Status == CustomLoopInvocationOperationStoreStatus.Created);
        Assert.Equal(7, outcomes.Count(item => item.Status == CustomLoopInvocationOperationStoreStatus.Replayed));
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.NotFound, missingCompletion.Status);
        await Assert.ThrowsAsync<FormatException>(() => store.BeginAsync(pending with { RequestHash = new string('0', CustomLoopLimits.Sha256HexCharacters) }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteAsync(pending));

        Directory.CreateDirectory(paths.CustomLoopInvocationOperationsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopInvocationOperationsPath, "invoke-corrupt.json"), "not-json");
        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync("invoke-corrupt"));
    }

    private static CustomLoopInvocationOperation Pending(string operationId, string prompt)
    {
        const string loopId = "loop-store";
        const int version = 2;
        var definitionHash = new string('a', CustomLoopLimits.Sha256HexCharacters);
        var requestHash = CustomLoopInvocationRequestHash.Compute(operationId, loopId, version, definitionHash, "embodysense.web", "web", "default", prompt, "OpenAiCodex", "test-model");
        return new CustomLoopInvocationOperation(
            CustomLoopInvocationOperation.CurrentSchemaVersion,
            operationId,
            requestHash,
            loopId,
            version,
            definitionHash,
            "embodysense.web",
            "web",
            "default",
            prompt,
            "OpenAiCodex",
            "test-model",
            Timestamp,
            Timestamp,
            CustomLoopInvocationOperationState.Pending,
            CustomLoopInvocationOutcome.Unknown,
            string.Empty,
            null,
            "The invocation is pending.");
    }

    private static CustomLoopInvocationOperation CompletedAdmitted(CustomLoopInvocationOperation pending)
    {
        return pending with
        {
            UpdatedAtUtc = Timestamp.AddSeconds(1),
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = CustomLoopInvocationOutcome.Admitted,
            AdmissionStatus = "Admitted",
            RunId = "run-admitted",
            Detail = "The run was admitted."
        };
    }
}
