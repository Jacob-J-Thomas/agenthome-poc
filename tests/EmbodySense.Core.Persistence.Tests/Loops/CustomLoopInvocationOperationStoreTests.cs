using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Models;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopInvocationOperationStoreTests
{
    private static readonly DateTimeOffset _timestamp = new(2026, 7, 16, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Pending_receipt_binds_context_once_and_conflicts_on_a_different_conversation()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopInvocationOperationStore(new WorkspacePaths(workspace.RootPath));
        var pending = Pending("invoke-context-binding", "secret prompt");
        var bound = ContextBound(pending);

        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(pending)).Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, (await store.BindAsync(bound)).Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Replayed, (await store.BindAsync(bound)).Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Conflict, (await store.BindAsync(bound with { InvokingConversationId = new string('d', CustomLoopLimits.Sha256HexCharacters) })).Status);
        var loaded = Assert.IsType<CustomLoopInvocationOperation>(await store.GetAsync(pending.OperationId));
        Assert.Equal(CustomLoopInvocationBindingState.CapturedContext, loaded.BindingState);
        Assert.Equal(bound.InvokingConversationId, loaded.InvokingConversationId);
        Assert.Equal(bound.ContextIdentityHash, loaded.ContextIdentityHash);

        var terminalPending = Pending("invoke-terminal-binding", "secret prompt");
        var terminal = ConversationBound(terminalPending, CustomLoopInvocationBindingState.ConversationWorkspaceExecutionBusy);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(terminalPending)).Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, (await store.BindAsync(terminal)).Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Conflict, (await store.BindAsync(ContextBound(terminalPending))).Status);

        var capturedPending = Pending("invoke-captured-not-found", "secret prompt");
        var captured = ContextBound(capturedPending);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(capturedPending)).Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, (await store.BindAsync(captured)).Status);
        var terminalized = captured with { BindingState = CustomLoopInvocationBindingState.CapturedContextNotFound, Detail = "The definition disappeared after context capture." };
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, (await store.BindAsync(terminalized)).Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Conflict, (await store.BindAsync(captured)).Status);
    }

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
        var bound = await BindConversationAsync(first, pending, CustomLoopInvocationBindingState.ConversationWorkspaceExecutionBusy);
        var completed = bound with
        {
            UpdatedAtUtc = _timestamp.AddSeconds(1),
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
        Assert.Equal(completed with { ValidationErrors = loaded!.ValidationErrors }, loaded);
        Assert.Empty(loaded.ValidationErrors);
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
        var pending = Pending("invoke-chronology", "prompt") with { UpdatedAtUtc = _timestamp.AddSeconds(5) };
        await store.BeginAsync(pending);
        pending = await BindContextAsync(store, pending);

        var regressed = CompletedAdmitted(pending) with { CreatedAtUtc = _timestamp.AddMinutes(-1), UpdatedAtUtc = _timestamp.AddSeconds(4) };
        var conflict = await store.CompleteAsync(regressed);
        var completed = await store.CompleteAsync(regressed with { UpdatedAtUtc = _timestamp.AddSeconds(6) });
        var replayed = await store.CompleteAsync(regressed with { UpdatedAtUtc = _timestamp.AddSeconds(6) });

        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Conflict, conflict.Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Completed, completed.Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Replayed, replayed.Status);
        Assert.Equal(_timestamp, completed.Operation!.CreatedAtUtc);
        Assert.Equal(_timestamp.AddSeconds(6), completed.Operation.UpdatedAtUtc);
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

        Assert.Equal(CustomLoopInvocationOperationStoreStatus.LimitExceeded, (await store.BeginAsync(Pending("invoke-over-quota", "prompt"))).Status);
    }

    [Fact]
    public async Task Completion_applies_the_workspace_byte_quota_to_the_replacement_delta()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopInvocationOperationStore(paths);
        var pending = Pending("invoke-completion-quota", "prompt");
        await store.BeginAsync(pending);
        pending = await BindContextAsync(store, pending);
        var receiptPath = Path.Combine(paths.CustomLoopInvocationOperationsPath, pending.OperationId + ".json");
        var quotaPath = Path.Combine(paths.CustomLoopInvocationOperationsPath, "existing-quota.json");
        await using (var quota = new FileStream(quotaPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            quota.SetLength(CustomLoopLimits.MaxInvocationOperationWorkspaceUtf8Bytes - new FileInfo(receiptPath).Length);
        }

        var expanded = CompletedAdmitted(pending) with { Detail = new string('x', CustomLoopLimits.MaxRunDetailCharacters) };

        Assert.Equal(CustomLoopInvocationOperationStoreStatus.LimitExceeded, (await store.CompleteAsync(expanded)).Status);
        var persistedPending = Assert.IsType<CustomLoopInvocationOperation>(await store.GetAsync(pending.OperationId));
        Assert.Equal(pending with { ValidationErrors = persistedPending.ValidationErrors }, persistedPending);
        Assert.Empty(persistedPending.ValidationErrors);
    }

    [Fact]
    public async Task Governed_retention_prunes_only_completed_receipts_at_the_replay_boundary()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var now = _timestamp.AddDays(30).AddSeconds(1);
        var time = new MutableTimeProvider(now);
        var store = new CustomLoopInvocationOperationStore(paths, time);
        await PersistCompletedAsync(store, "invoke-expired", _timestamp.AddSeconds(1));
        await PersistCompletedAsync(store, "invoke-newer", _timestamp.AddSeconds(2));
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(Pending("invoke-pending-retained", "pending"))).Status);
        var request = RetentionRequest(now);

        var reserved = await store.ReserveCompletedReceiptRetentionAsync(request);

        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.Reserved, reserved.Status);
        var candidate = Assert.Single(reserved.Operation!.Candidates);
        Assert.Equal("invoke-expired", candidate.OperationId);
        Assert.Equal(_timestamp.AddSeconds(1), candidate.CompletedAtUtc);
        Assert.True(candidate.ArtifactUtf8Bytes > 0);
        Assert.Equal(CustomLoopLimits.Sha256HexCharacters, candidate.ArtifactHash.Length);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.RetentionRequired, (await store.BeginAsync(Pending("invoke-blocked-during-retention", "blocked"))).Status);

        var intent = await store.MarkReceiptRetentionIntentAuditedAsync(request.OperationId, now.AddSeconds(1));
        var committed = await store.CommitCompletedReceiptRetentionAsync(request.OperationId, now.AddSeconds(2));
        var auditStarted = await store.MarkReceiptRetentionOutcomeAuditStartedAsync(request.OperationId, now.AddSeconds(3));
        var audited = await store.MarkReceiptRetentionOutcomeAuditedAsync(request.OperationId, now.AddSeconds(4));

        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.IntentAuditRecorded, intent.State);
        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.OutcomeCommitted, committed.State);
        Assert.Equal(1, committed.DeletedReceiptCount);
        Assert.Equal(candidate.ArtifactUtf8Bytes, committed.DeletedReceiptUtf8Bytes);
        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditStarted, auditStarted.State);
        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded, audited.State);
        Assert.Null(await store.GetAsync("invoke-expired"));
        Assert.NotNull(await store.GetAsync("invoke-newer"));
        Assert.NotNull(await store.GetAsync("invoke-pending-retained"));
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(Pending("invoke-expired", "fresh after boundary"))).Status);
    }

    [Fact]
    public async Task Retention_abandons_and_reselects_when_a_completed_receipt_changes_after_reservation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var now = _timestamp.AddDays(31);
        var store = new CustomLoopInvocationOperationStore(paths, new MutableTimeProvider(now));
        await PersistCompletedAsync(store, "invoke-changed", _timestamp.AddSeconds(1));
        await PersistCompletedAsync(store, "invoke-still-expired", _timestamp.AddSeconds(2));
        var request = RetentionRequest(now);
        var reserved = Assert.IsType<CustomLoopInvocationReceiptRetentionOperation>((await store.ReserveCompletedReceiptRetentionAsync(request)).Operation);
        await store.MarkReceiptRetentionIntentAuditedAsync(reserved.OperationId, now.AddSeconds(1));
        var changedPath = Path.Combine(paths.CustomLoopInvocationOperationsPath, "invoke-changed.json");
        var changed = JsonNode.Parse(await File.ReadAllTextAsync(changedPath))!.AsObject();
        changed["updatedAtUtc"] = now;
        changed["detail"] = "The completed receipt changed after retention reserved it.";
        await File.WriteAllTextAsync(changedPath, changed.ToJsonString());
        var abandoned = await store.CommitCompletedReceiptRetentionAsync(reserved.OperationId, now.AddSeconds(2));

        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.AbandonedCandidateChanged, abandoned.State);
        Assert.Equal(0, abandoned.DeletedReceiptCount);
        Assert.NotNull(await store.GetAsync("invoke-changed"));
        Assert.NotNull(await store.GetAsync("invoke-still-expired"));

        await store.MarkReceiptRetentionConflictAuditStartedAsync(reserved.OperationId, now.AddSeconds(3));
        await store.MarkReceiptRetentionConflictAuditedAsync(reserved.OperationId, now.AddSeconds(4));
        var reselection = await store.ReserveCompletedReceiptRetentionAsync(RetentionRequest(now.AddSeconds(3)));
        var replacement = Assert.IsType<CustomLoopInvocationReceiptRetentionOperation>(reselection.Operation);
        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.Reserved, reselection.Status);
        Assert.Equal("invoke-still-expired", Assert.Single(replacement.Candidates).OperationId);
        await store.MarkReceiptRetentionIntentAuditedAsync(replacement.OperationId, now.AddSeconds(5));
        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.OutcomeCommitted, (await store.CommitCompletedReceiptRetentionAsync(replacement.OperationId, now.AddSeconds(6))).State);
        Assert.NotNull(await store.GetAsync("invoke-changed"));
        Assert.Null(await store.GetAsync("invoke-still-expired"));
    }

    [Fact]
    public async Task Retention_never_selects_pending_receipts_and_reports_nothing_eligible()
    {
        using var workspace = new TestWorkspace();
        var now = _timestamp.AddDays(90);
        var store = new CustomLoopInvocationOperationStore(new WorkspacePaths(workspace.RootPath), new MutableTimeProvider(now));
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(Pending("invoke-old-pending", "pending"))).Status);

        var result = await store.ReserveCompletedReceiptRetentionAsync(RetentionRequest(now));

        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.NothingEligible, result.Status);
        Assert.Null(result.Operation);
        Assert.NotNull(await store.GetAsync("invoke-old-pending"));
    }

    [Fact]
    public async Task Retention_is_cross_process_serialized_and_reports_an_unexplained_missing_candidate_as_conflict()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var now = _timestamp.AddDays(31);
        var time = new MutableTimeProvider(now);
        var first = new CustomLoopInvocationOperationStore(paths, time);
        await PersistCompletedAsync(first, "invoke-crash-recovery", _timestamp.AddSeconds(1));
        var request = RetentionRequest(now);
        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.Reserved, (await first.ReserveCompletedReceiptRetentionAsync(request)).Status);
        var second = new CustomLoopInvocationOperationStore(paths, time);

        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.OperationInProgress, (await second.ReserveCompletedReceiptRetentionAsync(RetentionRequest(now))).Status);

        await first.MarkReceiptRetentionIntentAuditedAsync(request.OperationId, now.AddSeconds(1));
        File.Delete(Path.Combine(paths.CustomLoopInvocationOperationsPath, "invoke-crash-recovery.json"));
        time.UtcNow = now + CustomLoopInvocationReceiptRetentionPolicy.StaleRecoveryWindow + TimeSpan.FromSeconds(1);
        var resumed = await second.ReserveCompletedReceiptRetentionAsync(RetentionRequest(time.UtcNow));
        var committed = await second.CommitCompletedReceiptRetentionAsync(request.OperationId, time.UtcNow.AddSeconds(1));

        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.ReadyToCommit, resumed.Status);
        Assert.Equal(time.UtcNow, resumed.Operation!.OwnershipStartedAtUtc);
        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.OperationInProgress, (await first.ReserveCompletedReceiptRetentionAsync(RetentionRequest(time.UtcNow))).Status);
        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.AbandonedCandidateChanged, committed.State);
        Assert.Equal(0, committed.DeletedReceiptCount);
        Assert.Equal(0, committed.DeletedReceiptUtf8Bytes);

        time.UtcNow += CustomLoopInvocationReceiptRetentionPolicy.StaleRecoveryWindow + TimeSpan.FromSeconds(1);
        var conflict = await first.ReserveCompletedReceiptRetentionAsync(RetentionRequest(time.UtcNow));
        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.ConflictPendingAudit, conflict.Status);
        await first.MarkReceiptRetentionConflictAuditStartedAsync(request.OperationId, time.UtcNow.AddSeconds(1));
        await first.MarkReceiptRetentionConflictAuditedAsync(request.OperationId, time.UtcNow.AddSeconds(2));
        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.NothingEligible, (await first.ReserveCompletedReceiptRetentionAsync(RetentionRequest(time.UtcNow.AddSeconds(3)))).Status);
    }

    [Fact]
    public async Task Retention_timestamps_new_ownership_when_the_reservation_is_persisted()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var requestedAtUtc = _timestamp.AddDays(31);
        var time = new MutableTimeProvider(requestedAtUtc.AddSeconds(12));
        var store = new CustomLoopInvocationOperationStore(paths, time);
        await PersistCompletedAsync(store, "invoke-reservation-clock", _timestamp.AddSeconds(1));

        var reserved = await store.ReserveCompletedReceiptRetentionAsync(RetentionRequest(requestedAtUtc));

        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.Reserved, reserved.Status);
        Assert.Equal(requestedAtUtc, reserved.Operation!.RequestedAtUtc);
        Assert.Equal(time.UtcNow, reserved.Operation.OwnershipStartedAtUtc);
        Assert.Equal(time.UtcNow, reserved.Operation.UpdatedAtUtc);
    }

    [Fact]
    public async Task Interrupted_outcome_audit_becomes_a_durable_warning_without_repeating_the_batch()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var now = _timestamp.AddDays(31);
        var time = new MutableTimeProvider(now);
        var first = new CustomLoopInvocationOperationStore(paths, time);
        await PersistCompletedAsync(first, "invoke-outcome-warning", _timestamp.AddSeconds(1));
        var request = RetentionRequest(now);
        var reserved = Assert.IsType<CustomLoopInvocationReceiptRetentionOperation>((await first.ReserveCompletedReceiptRetentionAsync(request)).Operation);
        await first.MarkReceiptRetentionIntentAuditedAsync(reserved.OperationId, now.AddSeconds(1));
        await first.CommitCompletedReceiptRetentionAsync(reserved.OperationId, now.AddSeconds(2));
        await first.MarkReceiptRetentionOutcomeAuditStartedAsync(reserved.OperationId, now.AddSeconds(3));
        time.UtcNow = now + CustomLoopInvocationReceiptRetentionPolicy.StaleRecoveryWindow + TimeSpan.FromSeconds(1);

        var recovered = await new CustomLoopInvocationOperationStore(paths, time).ReserveCompletedReceiptRetentionAsync(RetentionRequest(time.UtcNow));

        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.OutcomeCommitted, recovered.Status);
        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.CommittedWithAuditWarning, recovered.Operation!.State);
        Assert.Equal(1, recovered.Operation.DeletedReceiptCount);
        Assert.Null(await first.GetAsync("invoke-outcome-warning"));
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await first.BeginAsync(Pending("invoke-after-outcome-warning", "new receipt"))).Status);

        var preserved = await new CustomLoopInvocationOperationStore(paths, time).ReserveCompletedReceiptRetentionAsync(RetentionRequest(time.UtcNow.AddSeconds(1)));
        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.OutcomeCommitted, preserved.Status);
        Assert.Equal(reserved.OperationId, preserved.Operation!.OperationId);
        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.CommittedWithAuditWarning, preserved.Operation.State);
        Assert.Equal(1, preserved.Operation.DeletedReceiptCount);
    }

    [Fact]
    public async Task Receipt_and_retention_scans_remove_only_recognizable_stale_atomic_write_temps()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var operationTemp = Path.Combine(paths.CustomLoopInvocationOperationsPath, $".invoke-interrupted.json.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(paths.CustomLoopInvocationOperationsPath);
        await File.WriteAllTextAsync(operationTemp, "partial operation");
        var now = _timestamp.AddDays(31);
        var store = new CustomLoopInvocationOperationStore(paths, new MutableTimeProvider(now));

        await PersistCompletedAsync(store, "invoke-interrupted", _timestamp.AddSeconds(1));

        Assert.False(File.Exists(operationTemp));
        var retentionTemp = Path.Combine(paths.CustomLoopInvocationReceiptRetentionPath, $".active.json.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(paths.CustomLoopInvocationReceiptRetentionPath);
        await File.WriteAllTextAsync(retentionTemp, "partial retention journal");

        var reserved = await store.ReserveCompletedReceiptRetentionAsync(RetentionRequest(now));

        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.Reserved, reserved.Status);
        Assert.False(File.Exists(retentionTemp));
    }

    [Fact]
    public async Task Receipt_quota_and_retention_reject_unrecognized_store_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopInvocationOperationsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopInvocationOperationsPath, "unexpected.bin"), "unaccounted");
        var store = new CustomLoopInvocationOperationStore(paths);

        await Assert.ThrowsAsync<FormatException>(() => store.BeginAsync(Pending("invoke-unsafe-store", "prompt")));
        await Assert.ThrowsAsync<FormatException>(() => store.ReserveCompletedReceiptRetentionAsync(RetentionRequest(_timestamp.AddDays(31))));
    }

    [Fact]
    public async Task Retention_fails_closed_on_malformed_receipts_before_deleting_any_candidate()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var now = _timestamp.AddDays(31);
        var store = new CustomLoopInvocationOperationStore(paths, new MutableTimeProvider(now));
        await PersistCompletedAsync(store, "invoke-valid-expired", _timestamp.AddSeconds(1));
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopInvocationOperationsPath, "invoke-malformed.json"), "not-json");

        await Assert.ThrowsAsync<FormatException>(() => store.ReserveCompletedReceiptRetentionAsync(RetentionRequest(now)));

        Assert.NotNull(await store.GetAsync("invoke-valid-expired"));
        Assert.False(Directory.Exists(paths.CustomLoopInvocationReceiptRetentionPath));
    }

    [Fact]
    public async Task Concurrent_same_operation_has_one_creator_and_validation_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopInvocationOperationStore(paths);
        var pending = Pending("invoke-concurrent", "prompt");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.BeginAsync(pending)));
        var missingCompletion = await store.CompleteAsync(CompletedAdmitted(ContextBound(Pending("invoke-missing", "prompt"))));

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
        pending = await BindContextAsync(store, pending);
        var contradictory = pending with
        {
            UpdatedAtUtc = _timestamp.AddSeconds(1),
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
        pending = admissionStatus == "NotFound"
            ? await BindConversationAsync(store, pending, CustomLoopInvocationBindingState.ConversationNotFound)
            : await BindContextAsync(store, pending);
        var rejected = pending with
        {
            UpdatedAtUtc = _timestamp.AddSeconds(1),
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = CustomLoopInvocationOutcome.Rejected,
            AdmissionStatus = admissionStatus,
            RunId = runId,
            Detail = "The invocation was rejected."
        };

        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Completed, (await store.CompleteAsync(rejected)).Status);
    }

    [Fact]
    public async Task Rejected_completion_round_trips_bounded_validation_errors()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopInvocationOperationStore(paths);
        var pending = Pending("invoke-validation-errors", "prompt");
        await store.BeginAsync(pending);
        pending = await BindContextAsync(store, pending);
        var errors = Enumerable.Range(0, CustomLoopLimits.MaxInvocationValidationErrors)
            .Select(index => new CustomLoopValidationError(
                new string('c', CustomLoopLimits.MaxInvocationValidationErrorCodeCharacters - 2) + index.ToString("D2"),
                new string('f', CustomLoopLimits.MaxInvocationValidationErrorFieldCharacters - 2) + index.ToString("D2"),
                new string('m', CustomLoopLimits.MaxInvocationValidationErrorMessageCharacters - 2) + index.ToString("D2")))
            .ToArray();
        var rejected = pending with
        {
            UpdatedAtUtc = _timestamp.AddSeconds(1),
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = CustomLoopInvocationOutcome.Rejected,
            AdmissionStatus = "Invalid",
            ValidationErrors = errors,
            Detail = new string('\u0001', CustomLoopLimits.MaxRunDetailCharacters)
        };

        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Completed, (await store.CompleteAsync(rejected)).Status);
        var restarted = new CustomLoopInvocationOperationStore(paths);
        var loaded = Assert.IsType<CustomLoopInvocationOperation>(await restarted.GetAsync(pending.OperationId));
        Assert.Equal(errors, loaded.ValidationErrors);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Replayed, (await restarted.CompleteAsync(rejected)).Status);
    }

    [Fact]
    public async Task Receipt_without_structured_validation_errors_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopInvocationOperationStore(paths);
        var pending = Pending("invoke-incompatible-shape", "prompt");
        await store.BeginAsync(pending);
        var path = Path.Combine(paths.CustomLoopInvocationOperationsPath, pending.OperationId + ".json");
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        persisted.Remove("validationErrors");
        await File.WriteAllTextAsync(path, persisted.ToJsonString());

        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(pending.OperationId));
    }

    [Fact]
    public async Task Receipt_validation_rejects_unbounded_or_noncanonical_validation_errors()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopInvocationOperationStore(new WorkspacePaths(workspace.RootPath));
        var pending = Pending("invoke-invalid-errors", "prompt");
        await store.BeginAsync(pending);
        pending = await BindContextAsync(store, pending);
        var rejected = pending with
        {
            UpdatedAtUtc = _timestamp.AddSeconds(1),
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = CustomLoopInvocationOutcome.Rejected,
            AdmissionStatus = "Invalid",
            ValidationErrors = Enumerable.Range(0, CustomLoopLimits.MaxInvocationValidationErrors + 1).Select(index => new CustomLoopValidationError($"code-{index}", "field", "message")).ToArray(),
            Detail = "The invocation was rejected."
        };

        await Assert.ThrowsAsync<FormatException>(() => store.CompleteAsync(rejected));
        await Assert.ThrowsAsync<FormatException>(() => store.CompleteAsync(rejected with { ValidationErrors = [new CustomLoopValidationError("code", "field", "unsafe\nmessage")] }));
    }

    [Theory]
    [InlineData("code", "code", "D800")]
    [InlineData("code", "code", "DC00")]
    [InlineData("field", "field", "D800")]
    [InlineData("field", "field", "DC00")]
    [InlineData("message", "message", "D800")]
    [InlineData("message", "message", "DC00")]
    public async Task Persisted_validation_error_with_malformed_utf16_fails_through_canonical_format_validation(string propertyName, string value, string codeUnit)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopInvocationOperationStore(paths);
        var pending = Pending("invoke-malformed-validation-error-" + propertyName + codeUnit.ToLowerInvariant(), "prompt");
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(pending)).Status);
        pending = await BindContextAsync(store, pending);
        var rejected = pending with
        {
            UpdatedAtUtc = _timestamp.AddSeconds(1),
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = CustomLoopInvocationOutcome.Rejected,
            AdmissionStatus = "Invalid",
            ValidationErrors = [new CustomLoopValidationError("code", "field", "message")],
            Detail = "The invocation was rejected."
        };
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Completed, (await store.CompleteAsync(rejected)).Status);

        var path = Path.Combine(paths.CustomLoopInvocationOperationsPath, pending.OperationId + ".json");
        var persisted = await File.ReadAllTextAsync(path);
        var malformed = persisted.Replace("\"" + propertyName + "\": \"" + value + "\"", "\"" + propertyName + "\": \"\\u" + codeUnit + "\"", StringComparison.Ordinal);
        Assert.NotEqual(persisted, malformed);
        await File.WriteAllTextAsync(path, malformed);

        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(pending.OperationId));
    }

    [Fact]
    public async Task Invocation_receipt_reports_missing_and_invalid_binding_transitions_without_creating_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopInvocationOperationStore(paths);
        var missing = Pending("invoke-missing-binding", "prompt");

        await Assert.ThrowsAsync<ArgumentException>(() => store.BeginAsync(ContextBound(missing)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.BindAsync(missing));
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.NotFound, (await store.BindAsync(ContextBound(missing))).Status);
        Assert.Empty(Directory.EnumerateFiles(paths.CustomLoopInvocationOperationsPath, "*.json", SearchOption.TopDirectoryOnly));

        var pending = Pending("invoke-binding-regression", "prompt") with { UpdatedAtUtc = _timestamp.AddSeconds(2) };
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(pending)).Status);
        var regressed = ContextBound(pending) with { UpdatedAtUtc = _timestamp.AddSeconds(1) };
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Conflict, (await store.BindAsync(regressed)).Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, (await store.BindAsync(ContextBound(pending))).Status);
    }

    [Fact]
    public async Task Retention_warning_transitions_are_idempotent_and_do_not_reopen_terminal_journals()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var now = _timestamp.AddDays(45);
        var time = new MutableTimeProvider(now);
        var store = new CustomLoopInvocationOperationStore(paths, time);
        await PersistCompletedAsync(store, "invoke-outcome-warning-transition", _timestamp.AddSeconds(1));
        var request = RetentionRequest(now);
        var reserved = Assert.IsType<CustomLoopInvocationReceiptRetentionOperation>((await store.ReserveCompletedReceiptRetentionAsync(request)).Operation);

        await store.MarkReceiptRetentionIntentAuditedAsync(reserved.OperationId, now.AddSeconds(1));
        await store.CommitCompletedReceiptRetentionAsync(reserved.OperationId, now.AddSeconds(2));
        await store.MarkReceiptRetentionOutcomeAuditStartedAsync(reserved.OperationId, now.AddSeconds(3));
        var warning = await store.MarkReceiptRetentionOutcomeAuditWarningAsync(reserved.OperationId, now.AddSeconds(4));
        var replay = await store.MarkReceiptRetentionOutcomeAuditWarningAsync(reserved.OperationId, now.AddSeconds(5));
        var retained = await store.ReserveCompletedReceiptRetentionAsync(RetentionRequest(now.AddSeconds(6)));

        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.CommittedWithAuditWarning, warning.State);
        Assert.Equal(warning.OperationId, replay.OperationId);
        Assert.Equal(warning.State, replay.State);
        Assert.Equal(warning.UpdatedAtUtc, replay.UpdatedAtUtc);
        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.OutcomeCommitted, retained.Status);
        Assert.Equal(warning.OperationId, retained.Operation!.OperationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkReceiptRetentionIntentAuditedAsync("receipt-retention-missing", now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkReceiptRetentionOutcomeAuditedAsync("another-retention-operation", now));
    }

    [Fact]
    public async Task Retention_conflict_warning_remains_terminal_after_a_changed_candidate()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var now = _timestamp.AddDays(46);
        var store = new CustomLoopInvocationOperationStore(paths, new MutableTimeProvider(now));
        await PersistCompletedAsync(store, "invoke-conflict-warning-transition", _timestamp.AddSeconds(1));
        var request = RetentionRequest(now);
        var reserved = Assert.IsType<CustomLoopInvocationReceiptRetentionOperation>((await store.ReserveCompletedReceiptRetentionAsync(request)).Operation);
        await store.MarkReceiptRetentionIntentAuditedAsync(reserved.OperationId, now.AddSeconds(1));
        var receiptPath = Path.Combine(paths.CustomLoopInvocationOperationsPath, "invoke-conflict-warning-transition.json");
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!.AsObject();
        receipt["detail"] = "The completed receipt changed after retention reserved it.";
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());

        var abandoned = await store.CommitCompletedReceiptRetentionAsync(reserved.OperationId, now.AddSeconds(2));
        await store.MarkReceiptRetentionConflictAuditStartedAsync(reserved.OperationId, now.AddSeconds(3));
        var warning = await store.MarkReceiptRetentionConflictAuditWarningAsync(reserved.OperationId, now.AddSeconds(4));
        var replay = await store.MarkReceiptRetentionConflictAuditWarningAsync(reserved.OperationId, now.AddSeconds(5));
        var retained = await store.ReserveCompletedReceiptRetentionAsync(RetentionRequest(now.AddSeconds(6)));

        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.AbandonedCandidateChanged, abandoned.State);
        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.AbandonedWithAuditWarning, warning.State);
        Assert.Equal(warning.OperationId, replay.OperationId);
        Assert.Equal(warning.State, replay.State);
        Assert.Equal(warning.UpdatedAtUtc, replay.UpdatedAtUtc);
        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.ConflictPendingAudit, retained.Status);
        Assert.Equal(warning.OperationId, retained.Operation!.OperationId);
    }

    [Fact]
    public async Task Stale_committed_and_conflict_audits_recover_with_their_durable_public_statuses()
    {
        using var completedWorkspace = new TestWorkspace();
        var completedPaths = new WorkspacePaths(completedWorkspace.RootPath);
        var completedNow = _timestamp.AddDays(47);
        var completedTime = new MutableTimeProvider(completedNow);
        var completedStore = new CustomLoopInvocationOperationStore(completedPaths, completedTime);
        await PersistCompletedAsync(completedStore, "invoke-stale-committed", _timestamp.AddSeconds(1));
        var completedRequest = RetentionRequest(completedNow);
        var completedReservation = Assert.IsType<CustomLoopInvocationReceiptRetentionOperation>((await completedStore.ReserveCompletedReceiptRetentionAsync(completedRequest)).Operation);
        await completedStore.MarkReceiptRetentionIntentAuditedAsync(completedReservation.OperationId, completedNow.AddSeconds(1));
        await completedStore.CommitCompletedReceiptRetentionAsync(completedReservation.OperationId, completedNow.AddSeconds(2));

        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.OperationInProgress, (await completedStore.ReserveCompletedReceiptRetentionAsync(RetentionRequest(completedNow))).Status);
        completedTime.UtcNow += CustomLoopInvocationReceiptRetentionPolicy.StaleRecoveryWindow + TimeSpan.FromSeconds(1);
        var completedRecovery = await completedStore.ReserveCompletedReceiptRetentionAsync(RetentionRequest(completedTime.UtcNow));

        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.OutcomeCommitted, completedRecovery.Status);
        Assert.Equal(completedTime.UtcNow, completedRecovery.Operation!.OwnershipStartedAtUtc);

        using var conflictWorkspace = new TestWorkspace();
        var conflictPaths = new WorkspacePaths(conflictWorkspace.RootPath);
        var conflictNow = _timestamp.AddDays(48);
        var conflictTime = new MutableTimeProvider(conflictNow);
        var conflictStore = new CustomLoopInvocationOperationStore(conflictPaths, conflictTime);
        await PersistCompletedAsync(conflictStore, "invoke-stale-conflict", _timestamp.AddSeconds(1));
        var conflictRequest = RetentionRequest(conflictNow);
        var conflictReservation = Assert.IsType<CustomLoopInvocationReceiptRetentionOperation>((await conflictStore.ReserveCompletedReceiptRetentionAsync(conflictRequest)).Operation);
        await conflictStore.MarkReceiptRetentionIntentAuditedAsync(conflictReservation.OperationId, conflictNow.AddSeconds(1));
        File.Delete(Path.Combine(conflictPaths.CustomLoopInvocationOperationsPath, "invoke-stale-conflict.json"));
        await conflictStore.CommitCompletedReceiptRetentionAsync(conflictReservation.OperationId, conflictNow.AddSeconds(2));
        await conflictStore.MarkReceiptRetentionConflictAuditStartedAsync(conflictReservation.OperationId, conflictNow.AddSeconds(3));

        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.OperationInProgress, (await conflictStore.ReserveCompletedReceiptRetentionAsync(RetentionRequest(conflictNow))).Status);
        conflictTime.UtcNow += CustomLoopInvocationReceiptRetentionPolicy.StaleRecoveryWindow + TimeSpan.FromSeconds(1);
        var conflictRecovery = await conflictStore.ReserveCompletedReceiptRetentionAsync(RetentionRequest(conflictTime.UtcNow));
        var terminal = await conflictStore.ReserveCompletedReceiptRetentionAsync(RetentionRequest(conflictTime.UtcNow.AddSeconds(1)));

        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.ConflictPendingAudit, conflictRecovery.Status);
        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.AbandonedWithAuditWarning, conflictRecovery.Operation!.State);
        Assert.Equal(CustomLoopInvocationReceiptRetentionReservationStatus.ConflictPendingAudit, terminal.Status);
        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.AbandonedWithAuditWarning, terminal.Operation!.State);
    }

    private static async Task<CustomLoopInvocationOperation> BindConversationAsync(CustomLoopInvocationOperationStore store, CustomLoopInvocationOperation pending, CustomLoopInvocationBindingState bindingState)
    {
        var result = await store.BindAsync(ConversationBound(pending, bindingState));
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, result.Status);
        return Assert.IsType<CustomLoopInvocationOperation>(result.Operation);
    }

    private static async Task PersistCompletedAsync(CustomLoopInvocationOperationStore store, string operationId, DateTimeOffset completedAtUtc)
    {
        var pending = Pending(operationId, operationId);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(pending)).Status);
        var bound = await BindContextAsync(store, pending);
        var completed = CompletedAdmitted(bound) with { UpdatedAtUtc = completedAtUtc, RunId = "run-" + operationId };
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
    }

    private static CustomLoopInvocationReceiptRetentionRequest RetentionRequest(DateTimeOffset now)
    {
        return new CustomLoopInvocationReceiptRetentionRequest("receipt-retention-test", "embodysense.web", "web", now, now - CustomLoopInvocationReceiptRetentionPolicy.MinimumReplayDuration);
    }

    private static async Task<CustomLoopInvocationOperation> BindContextAsync(CustomLoopInvocationOperationStore store, CustomLoopInvocationOperation pending)
    {
        var result = await store.BindAsync(ContextBound(pending));
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, result.Status);
        return Assert.IsType<CustomLoopInvocationOperation>(result.Operation);
    }

    private static CustomLoopInvocationOperation ConversationBound(CustomLoopInvocationOperation pending, CustomLoopInvocationBindingState bindingState)
    {
        return pending with
        {
            BindingState = bindingState,
            InvokingConversationId = new string('b', CustomLoopLimits.Sha256HexCharacters),
            ContextIdentityHash = null
        };
    }

    private static CustomLoopInvocationOperation ContextBound(CustomLoopInvocationOperation pending)
    {
        return pending with
        {
            BindingState = CustomLoopInvocationBindingState.CapturedContext,
            InvokingConversationId = new string('b', CustomLoopLimits.Sha256HexCharacters),
            ContextIdentityHash = new string('c', CustomLoopLimits.Sha256HexCharacters)
        };
    }

    private static CustomLoopInvocationOperation Pending(string operationId, string prompt)
    {
        const string LoopId = "loop-store";
        const int Version = 2;
        var definitionHash = new string('a', CustomLoopLimits.Sha256HexCharacters);
        var requestHash = CustomLoopInvocationRequestHash.Compute(operationId, LoopId, Version, definitionHash, "embodysense.web", "web", "default", prompt, "OpenAiCodex", "test-model");
        var promptHash = CustomLoopInvocationRequestHash.ComputePromptHash(prompt);
        return new CustomLoopInvocationOperation(
            CustomLoopInvocationOperation.CurrentSchemaVersion,
            operationId,
            requestHash,
            LoopId,
            Version,
            definitionHash,
            "embodysense.web",
            "web",
            "default",
            promptHash,
            "OpenAiCodex",
            "test-model",
            CustomLoopInvocationBindingState.Unbound,
            null,
            null,
            _timestamp,
            _timestamp,
            CustomLoopInvocationOperationState.Pending,
            CustomLoopInvocationOutcome.Unknown,
            string.Empty,
            null,
            [],
            "The invocation is pending.");
    }

    private static CustomLoopInvocationOperation CompletedAdmitted(CustomLoopInvocationOperation pending)
    {
        return pending with
        {
            UpdatedAtUtc = _timestamp.AddSeconds(1),
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = CustomLoopInvocationOutcome.Admitted,
            AdmissionStatus = "Admitted",
            RunId = "run-admitted",
            Detail = "The run was admitted."
        };
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

}
