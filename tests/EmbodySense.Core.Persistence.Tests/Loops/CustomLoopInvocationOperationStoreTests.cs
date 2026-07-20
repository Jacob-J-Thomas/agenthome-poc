using System.Text.Json.Nodes;
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
        var receiptPath = Path.Combine(paths.CustomLoopInvocationOperationsPath, pending.OperationId + ".json");
        Assert.True(File.Exists(receiptPath));
        Assert.DoesNotContain("first prompt", await File.ReadAllTextAsync(receiptPath), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(paths.CustomLoopInvocationOperationsPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Completion_preserves_creation_time_and_rejects_update_time_regression()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopInvocationOperationStore(paths);
        var pending = Pending("invoke-chronology", "prompt") with { UpdatedAtUtc = Timestamp.AddSeconds(5) };
        await store.BeginAsync(pending);

        var regressed = CompletedAdmitted(pending) with { CreatedAtUtc = Timestamp.AddMinutes(-1), UpdatedAtUtc = Timestamp.AddSeconds(4) };
        var conflict = await store.CompleteAsync(regressed);
        var completed = await store.CompleteAsync(regressed with { UpdatedAtUtc = Timestamp.AddSeconds(6) });
        var replayed = await store.CompleteAsync(regressed with { UpdatedAtUtc = Timestamp.AddSeconds(6) });

        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Conflict, conflict.Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Completed, completed.Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Replayed, replayed.Status);
        Assert.Equal(Timestamp, completed.Operation!.CreatedAtUtc);
        Assert.Equal(Timestamp.AddSeconds(6), completed.Operation.UpdatedAtUtc);
    }

    [Fact]
    public async Task Valid_maximum_detail_fits_and_workspace_receipt_quota_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopInvocationOperationStore(paths);
        var maximumDetail = Pending("invoke-maximum-detail", "prompt") with { Detail = new string('\u0001', CustomLoopLimits.MaxRunDetailCharacters) };

        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(maximumDetail)).Status);
        Assert.NotNull(await store.GetAsync(maximumDetail.OperationId));

        var quotaPath = Path.Combine(paths.CustomLoopInvocationOperationsPath, "existing-quota.json");
        await using (var quota = new FileStream(quotaPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            quota.SetLength(CustomLoopLimits.MaxInvocationOperationWorkspaceUtf8Bytes);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginAsync(Pending("invoke-over-quota", "prompt")));
    }

    [Fact]
    public async Task Completion_applies_the_workspace_byte_quota_to_the_replacement_delta()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopInvocationOperationStore(paths);
        var pending = Pending("invoke-completion-quota", "prompt");
        await store.BeginAsync(pending);
        var receiptPath = Path.Combine(paths.CustomLoopInvocationOperationsPath, pending.OperationId + ".json");
        var quotaPath = Path.Combine(paths.CustomLoopInvocationOperationsPath, "existing-quota.json");
        await using (var quota = new FileStream(quotaPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            quota.SetLength(CustomLoopLimits.MaxInvocationOperationWorkspaceUtf8Bytes - new FileInfo(receiptPath).Length);
        }

        var expanded = CompletedAdmitted(pending) with { Detail = new string('x', CustomLoopLimits.MaxRunDetailCharacters) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(expanded));
        Assert.Equal(pending, await store.GetAsync(pending.OperationId));
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

    [Theory]
    [InlineData("detail")]
    [InlineData("admissionStatus")]
    public async Task Null_required_receipt_text_is_reported_as_malformed(string propertyName)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopInvocationOperationStore(paths);
        var pending = Pending("invoke-null-text", "prompt");
        await store.BeginAsync(pending);
        var path = Path.Combine(paths.CustomLoopInvocationOperationsPath, pending.OperationId + ".json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        root[propertyName] = null;
        await File.WriteAllTextAsync(path, root.ToJsonString());

        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(pending.OperationId));
    }

    [Theory]
    [InlineData("Admitted", null)]
    [InlineData("WorkspaceExecutionBusy", null)]
    [InlineData("arbitrary", null)]
    [InlineData("NotFound", "run-contradictory")]
    [InlineData("LimitExceeded", "run-contradictory")]
    [InlineData("NonterminalRunExists", null)]
    public async Task Rejected_completion_rejects_contradictory_status_and_run_shapes(string admissionStatus, string? runId)
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopInvocationOperationStore(new WorkspacePaths(workspace.RootPath));
        var pending = Pending("invoke-rejected-shape", "prompt");
        await store.BeginAsync(pending);
        var contradictory = pending with
        {
            UpdatedAtUtc = Timestamp.AddSeconds(1),
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = CustomLoopInvocationOutcome.Rejected,
            AdmissionStatus = admissionStatus,
            RunId = runId,
            Detail = "The invocation was rejected."
        };

        await Assert.ThrowsAsync<FormatException>(() => store.CompleteAsync(contradictory));
    }

    [Theory]
    [InlineData("Invalid", null)]
    [InlineData("Conflict", "run-conflict")]
    [InlineData("NonterminalRunExists", "run-active")]
    [InlineData("LimitExceeded", null)]
    [InlineData("NotFound", null)]
    [InlineData("AuditUnavailable", "run-audit")]
    public async Task Rejected_completion_accepts_defined_status_and_run_shapes(string admissionStatus, string? runId)
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopInvocationOperationStore(new WorkspacePaths(workspace.RootPath));
        var pending = Pending("invoke-rejected-valid", "prompt");
        await store.BeginAsync(pending);
        var rejected = pending with
        {
            UpdatedAtUtc = Timestamp.AddSeconds(1),
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = CustomLoopInvocationOutcome.Rejected,
            AdmissionStatus = admissionStatus,
            RunId = runId,
            Detail = "The invocation was rejected."
        };

        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Completed, (await store.CompleteAsync(rejected)).Status);
    }

    private static CustomLoopInvocationOperation Pending(string operationId, string prompt)
    {
        const string loopId = "loop-store";
        const int version = 2;
        var definitionHash = new string('a', CustomLoopLimits.Sha256HexCharacters);
        var requestHash = CustomLoopInvocationRequestHash.Compute(operationId, loopId, version, definitionHash, "embodysense.web", "web", "default", prompt, "OpenAiCodex", "test-model");
        var promptHash = CustomLoopInvocationRequestHash.ComputePromptHash(prompt);
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
            promptHash,
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
