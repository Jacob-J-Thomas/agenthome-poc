using EmbodySense.Core.Application.Loops.Models;
using System.Diagnostics;
using System.Collections.Immutable;
using System.Security.Cryptography;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;
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

    [Fact]
    public async Task Cleanup_compacts_only_expired_audited_receipts_and_retains_an_explicit_expiry_fingerprint()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var completedAtUtc = _timestamp;
        var time = new MutableTimeProvider(completedAtUtc);
        var audit = new RecordingAuditLog();
        var store = new CustomLoopControlOperationStore(paths, audit, time);
        var pending = Pending("control-expired", AuditSchema.Actors.Web);
        var created = await store.BeginAsync(pending);
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, completedAtUtc);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        time.UtcNow = completedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var cleanup = await store.CleanupAsync(CleanupRequest("cleanup-expired", time.UtcNow));
        var lookup = await store.LookupOperationAsync(completed.OperationId);
        var expiredBegin = await store.BeginAsync(pending);
        var posture = await store.InspectAsync();

        Assert.True(cleanup.Status == CustomLoopReceiptCleanupStatus.Pruned, cleanup.Detail);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, $".{completed.OperationId}.owner.lock")));
        Assert.Equal(CustomLoopReceiptOperationLookupStatus.Expired, lookup.Status);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Expired, expiredBegin.Status);
        Assert.Equal(completed.RequestHash, lookup.ExpiredProof!.RequestHash);
        Assert.Equal(completedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration, lookup.ExpiredProof.ExpiredAtUtc);
        Assert.Equal(0, posture.ArtifactCount);
        Assert.Equal(1, posture.Categories.Single(item => item.Category == CustomLoopReceiptArtifactCategory.ExpiredIdempotency).ArtifactCount);
        Assert.Equal(2, audit.Events.Count);
    }

    [Fact]
    public async Task Cleanup_preserves_raw_receipts_when_an_unconfirmed_intent_audit_exceeds_its_owner_window()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var completedAtUtc = _timestamp;
        var time = new MutableTimeProvider(completedAtUtc);
        var pending = Pending("control-audit-gap", AuditSchema.Actors.Web);
        var initial = new CustomLoopControlOperationStore(paths, new ThrowingAuditLog(), time);
        var created = await initial.BeginAsync(pending);
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, completedAtUtc);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await initial.CompleteAsync(completed)).Status);
        lease.Dispose();

        time.UtcNow = completedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-audit-gap", time.UtcNow);
        var unavailable = await initial.CleanupAsync(request);
        Assert.Equal(CustomLoopReceiptCleanupStatus.AuditUnavailable, unavailable.Status);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
        Assert.Equal(CustomLoopReceiptCleanupStage.IntentAuditStarted, unavailable.Journal!.Stage);

        time.UtcNow += CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time).CleanupAsync(request);

        Assert.Equal(CustomLoopReceiptCleanupStatus.AuditUnavailable, recovered.Status);
        Assert.Equal(CustomLoopReceiptCleanupStage.Degraded, recovered.Journal!.Stage);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
        Assert.False(File.Exists(paths.CustomLoopReceiptProofLedgerPath));
    }

    [Fact]
    public async Task Distinct_lifecycle_admission_recovers_an_expired_cleanup_owner_without_repeating_uncertain_audit()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var initial = new CustomLoopControlOperationStore(paths, new ThrowingAuditLog(), time);
        var created = await initial.BeginAsync(Pending("control-stale-cleanup", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await initial.CompleteAsync(completed)).Status);
        lease.Dispose();

        time.UtcNow = _timestamp + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var interrupted = await initial.CleanupAsync(CleanupRequest("cleanup-stale-owner", time.UtcNow));
        Assert.Equal(CustomLoopReceiptCleanupStage.IntentAuditStarted, interrupted.Journal!.Stage);

        time.UtcNow += CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recoveredAudit = new RecordingAuditLog();
        var distinct = await new CustomLoopControlOperationStore(paths, recoveredAudit, time).BeginAsync(Pending("control-after-stale-cleanup", AuditSchema.Actors.Web));
        using var distinctLease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(distinct.Lease);

        Assert.Equal(CustomLoopControlOperationStoreStatus.Created, distinct.Status);
        Assert.Equal(CustomLoopReceiptCleanupStage.Degraded, ReadCleanupJournal(Path.Combine(paths.CustomLoopControlReceiptCleanupPath, "active.json")).Stage);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
        Assert.Empty(recoveredAudit.Events);
    }

    [Fact]
    public async Task Cleanup_recovers_a_confirmed_intent_audit_without_appending_duplicate_evidence_after_the_crash_boundary()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var completedAtUtc = _timestamp;
        var time = new MutableTimeProvider(completedAtUtc);
        var audit = new RecordThenThrowOnceAuditLog();
        var initial = new CustomLoopControlOperationStore(paths, audit, time);
        var created = await initial.BeginAsync(Pending("control-intent-audit-restart", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, completedAtUtc);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await initial.CompleteAsync(completed)).Status);
        lease.Dispose();

        time.UtcNow = completedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-intent-audit-restart", time.UtcNow);
        var interrupted = await initial.CleanupAsync(request);

        Assert.Equal(CustomLoopReceiptCleanupStatus.AuditUnavailable, interrupted.Status);
        Assert.Equal(CustomLoopReceiptCleanupStage.IntentAuditStarted, interrupted.Journal!.Stage);
        Assert.Single(audit.Events, item => item.Action == AuditSchema.Actions.LoopControlReceiptRetentionIntent);

        time.UtcNow += CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await new CustomLoopControlOperationStore(paths, audit, time).CleanupAsync(request);

        Assert.True(recovered.Status == CustomLoopReceiptCleanupStatus.Pruned, recovered.Detail);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
        Assert.Single(audit.Events, item => item.Action == AuditSchema.Actions.LoopControlReceiptRetentionIntent);
        Assert.Single(audit.Events, item => item.Action == AuditSchema.Actions.LoopControlReceiptRetentionOutcome);
    }

    [Fact]
    public async Task Cleanup_restarts_from_a_proof_ledger_failpoint_without_reselecting_or_replaying_the_lifecycle_operation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var completedAtUtc = _timestamp;
        var time = new MutableTimeProvider(completedAtUtc);
        var store = new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time);
        var created = await store.BeginAsync(Pending("control-proof-restart", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, completedAtUtc);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var replayExpiry = completedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-proof-restart", replayExpiry);
        var receiptPath = Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json");
        var journalPath = Path.Combine(paths.CustomLoopControlReceiptCleanupPath, "active.json");
        var crashTime = new BoundaryCrashTimeProvider(replayExpiry, () => File.Exists(paths.CustomLoopReceiptProofLedgerPath)
            && File.Exists(journalPath)
            && ReadCleanupJournal(journalPath).Stage == CustomLoopReceiptCleanupStage.IntentAuditRecorded);
        var interrupted = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), crashTime).CleanupAsync(request);

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, interrupted.Status);
        Assert.True(File.Exists(receiptPath));
        Assert.Equal(CustomLoopReceiptCleanupStage.IntentAuditRecorded, ReadCleanupJournal(journalPath).Stage);
        var ledger = CustomLoopReceiptRetentionContractCodec.DeserializeProofLedger(await File.ReadAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath));
        Assert.Equal(completed.UpdatedAtUtc, Assert.Single(ledger.ExpiredOperations).CompletedAtUtc);

        time.UtcNow = replayExpiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time).CleanupAsync(request);

        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, recovered.Status);
        Assert.False(File.Exists(receiptPath));
        Assert.Equal(CustomLoopReceiptOperationLookupStatus.Expired, (await store.LookupOperationAsync(completed.OperationId)).Status);
        var recoveredLedger = CustomLoopReceiptRetentionContractCodec.DeserializeProofLedger(await File.ReadAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath));
        Assert.Equal(1, recoveredLedger.Generation);
        Assert.Single(recoveredLedger.ExpiredOperations);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Cleanup_reconstructs_and_attributes_each_actual_removal_before_journal_progress_window(int crashAfterRemovalCount)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var completedAtUtc = _timestamp;
        var creationTime = new MutableTimeProvider(completedAtUtc);
        var creationStore = new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), creationTime);
        var completed = new List<CustomLoopControlOperation>();
        foreach (var operationId in new[] { "control-removal-a", "control-removal-b", "control-removal-c" })
        {
            var created = await creationStore.BeginAsync(Pending(operationId, AuditSchema.Actors.Web));
            using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
            var terminal = Complete(created.Operation!, completedAtUtc);
            Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await creationStore.CompleteAsync(terminal)).Status);
            lease.Dispose();
            completed.Add(terminal);
        }

        var replayExpiry = completedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest($"cleanup-removal-{crashAfterRemovalCount}", replayExpiry);
        var receiptPaths = completed.Select(item => Path.Combine(paths.CustomLoopControlOperationsPath, item.OperationId + ".json")).ToArray();
        var journalPath = Path.Combine(paths.CustomLoopControlReceiptCleanupPath, "active.json");
        var crashTime = new BoundaryCrashTimeProvider(replayExpiry, () => IsRemovalWriteAheadOfJournal(journalPath, receiptPaths, crashAfterRemovalCount));
        var interrupted = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), crashTime).CleanupAsync(request);

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, interrupted.Status);
        Assert.Equal(crashAfterRemovalCount, receiptPaths.Count(path => !File.Exists(path)));
        var interruptedJournal = ReadCleanupJournal(journalPath);
        Assert.Equal(CustomLoopReceiptCleanupStage.ProofLedgerWritten, interruptedJournal.Stage);
        Assert.Equal(crashAfterRemovalCount - 1, interruptedJournal.RemovedArtifactCount);

        var firstRecoveryAtUtc = replayExpiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var reconstructionCrashTime = new BoundaryCrashTimeProvider(firstRecoveryAtUtc, () => IsReconstructedProgressBeforeNextStage(journalPath, receiptPaths, crashAfterRemovalCount));
        var reconstructionInterrupted = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), reconstructionCrashTime).CleanupAsync(request);

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, reconstructionInterrupted.Status);
        var reconstructedJournal = ReadCleanupJournal(journalPath);
        Assert.Equal(CustomLoopReceiptCleanupStage.ProofLedgerWritten, reconstructedJournal.Stage);
        Assert.Equal(crashAfterRemovalCount, reconstructedJournal.RemovedArtifactCount);
        Assert.Equal(reconstructedJournal.Candidates.OrderBy(item => item.ArtifactId, StringComparer.Ordinal).Take(crashAfterRemovalCount).Sum(item => item.ArtifactUtf8Bytes), reconstructedJournal.RemovedArtifactUtf8Bytes);

        var finalRecoveryAtUtc = firstRecoveryAtUtc + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), new MutableTimeProvider(finalRecoveryAtUtc)).CleanupAsync(request);

        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, recovered.Status);
        Assert.Equal(completed.Count, recovered.CompactedArtifactCount);
        Assert.Equal(recovered.Journal!.Candidates.Sum(item => item.ArtifactUtf8Bytes), recovered.CompactedArtifactUtf8Bytes);
        Assert.All(receiptPaths, path => Assert.False(File.Exists(path)));
        foreach (var operation in completed)
        {
            Assert.Equal(CustomLoopReceiptOperationLookupStatus.Expired, (await creationStore.LookupOperationAsync(operation.OperationId)).Status);
        }
    }

    [Fact]
    public async Task Cleanup_rejects_future_request_time_without_accelerating_exact_replay_or_creating_a_lease()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var completedAtUtc = _timestamp;
        var creationStore = new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), new MutableTimeProvider(completedAtUtc));
        var created = await creationStore.BeginAsync(Pending("control-future-cleanup", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, completedAtUtc);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await creationStore.CompleteAsync(completed)).Status);
        lease.Dispose();

        var trustedNow = completedAtUtc.AddDays(1);
        var futureRequest = CleanupRequest("cleanup-future-time", completedAtUtc.AddDays(31));
        var audit = new RecordingAuditLog();
        var store = new CustomLoopControlOperationStore(paths, audit, new MutableTimeProvider(trustedNow));
        var result = await store.CleanupAsync(futureRequest);

        Assert.Equal(CustomLoopReceiptCleanupStatus.Invalid, result.Status);
        Assert.Equal(CustomLoopReceiptOperationLookupStatus.Exact, (await store.LookupOperationAsync(completed.OperationId)).Status);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
        Assert.False(File.Exists(paths.CustomLoopReceiptProofLedgerPath));
        Assert.False(Directory.Exists(paths.CustomLoopControlReceiptCleanupPath));
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Cleanup_abandons_a_recovered_intent_when_its_selected_receipt_changed_before_proof_commit()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var completedAtUtc = _timestamp;
        var time = new MutableTimeProvider(completedAtUtc);
        var store = new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time);
        var created = await store.BeginAsync(Pending("control-cleanup-conflict", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, completedAtUtc);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var replayExpiry = completedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-conflict", replayExpiry);
        var receiptPath = Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json");
        var receiptBytes = await File.ReadAllBytesAsync(receiptPath);
        var receiptHash = Convert.ToHexString(SHA256.HashData(receiptBytes)).ToLowerInvariant();
        var proof = new CustomLoopExpiredOperationProof(CustomLoopExpiredOperationProof.CurrentSchemaVersion, CustomLoopReceiptArtifactClass.LifecycleControlReceipt, completed.OperationId, completed.RequestHash, receiptHash, completedAtUtc, replayExpiry);
        var candidate = new CustomLoopReceiptCleanupCandidate(completed.OperationId, receiptHash, receiptBytes.Length, CustomLoopReceiptArtifactCategory.Compactable, true, true, proof, null);
        var journal = new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner",
            Environment.ProcessId,
            replayExpiry,
            CustomLoopReceiptCleanupStage.IntentAuditRecorded,
            CustomLoopReceiptCleanupOutcome.Unknown,
            replayExpiry,
            [candidate],
            null,
            0,
            0,
            "Failpoint after intent audit and before compact proof persistence.");
        Directory.CreateDirectory(paths.CustomLoopControlReceiptCleanupPath);
        await File.WriteAllBytesAsync(Path.Combine(paths.CustomLoopControlReceiptCleanupPath, "active.json"), CustomLoopReceiptRetentionContractCodec.SerializeCleanupJournal(journal));
        await File.WriteAllBytesAsync(receiptPath, [.. receiptBytes, (byte)'\n']);

        time.UtcNow = replayExpiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time).CleanupAsync(request);

        Assert.Equal(CustomLoopReceiptCleanupStatus.CleanupConflict, recovered.Status);
        Assert.Equal(CustomLoopReceiptCleanupStage.AbandonedConflict, recovered.Journal!.Stage);
        Assert.True(File.Exists(receiptPath));
        Assert.False(File.Exists(paths.CustomLoopReceiptProofLedgerPath));
    }

    [Fact]
    public async Task Cleanup_does_not_repeat_an_uncertain_outcome_audit_after_removal_recovery()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var completedAtUtc = _timestamp;
        var time = new MutableTimeProvider(completedAtUtc);
        var store = new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time);
        var created = await store.BeginAsync(Pending("control-outcome-restart", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, completedAtUtc);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var replayExpiry = completedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-outcome-restart", replayExpiry);
        var receiptPath = Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json");
        var receiptBytes = await File.ReadAllBytesAsync(receiptPath);
        var receiptHash = Convert.ToHexString(SHA256.HashData(receiptBytes)).ToLowerInvariant();
        var proof = new CustomLoopExpiredOperationProof(CustomLoopExpiredOperationProof.CurrentSchemaVersion, CustomLoopReceiptArtifactClass.LifecycleControlReceipt, completed.OperationId, completed.RequestHash, receiptHash, completedAtUtc, replayExpiry);
        var ledger = new CustomLoopReceiptProofLedger(CustomLoopReceiptProofLedger.CurrentSchemaVersion, 1, replayExpiry, null, ImmutableArray<CustomLoopDefinitionLineageProof>.Empty, [proof]);
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath, CustomLoopReceiptRetentionContractCodec.SerializeProofLedger(ledger));
        var candidate = new CustomLoopReceiptCleanupCandidate(completed.OperationId, receiptHash, receiptBytes.Length, CustomLoopReceiptArtifactCategory.Compactable, true, true, proof, null);
        var journal = new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner",
            Environment.ProcessId,
            replayExpiry,
            CustomLoopReceiptCleanupStage.OutcomeAuditStarted,
            CustomLoopReceiptCleanupOutcome.Unknown,
            replayExpiry,
            [candidate],
            CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(ledger),
            1,
            receiptBytes.Length,
            "Failpoint after raw receipt removal and before outcome audit confirmation.");
        Directory.CreateDirectory(paths.CustomLoopControlReceiptCleanupPath);
        await File.WriteAllBytesAsync(Path.Combine(paths.CustomLoopControlReceiptCleanupPath, "active.json"), CustomLoopReceiptRetentionContractCodec.SerializeCleanupJournal(journal));
        File.Delete(receiptPath);

        time.UtcNow = replayExpiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recoveredAudit = new RecordingAuditLog();
        var recovered = await new CustomLoopControlOperationStore(paths, recoveredAudit, time).CleanupAsync(request);

        Assert.Equal(CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning, recovered.Status);
        Assert.Equal(CustomLoopReceiptCleanupStage.CommittedWithAuditWarning, recovered.Journal!.Stage);
        Assert.Empty(recoveredAudit.Events);
        Assert.Equal(CustomLoopReceiptOperationLookupStatus.Expired, (await store.LookupOperationAsync(completed.OperationId)).Status);
    }

    [Fact]
    public async Task Raw_receipt_and_compact_proof_contradiction_fails_closed_before_exact_or_replayed_results()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), new MutableTimeProvider(_timestamp));
        var pending = Pending("control-contradictory-proof", AuditSchema.Actors.Web);
        var created = await store.BeginAsync(pending);
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var receiptPath = Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json");
        var receiptBytes = await File.ReadAllBytesAsync(receiptPath);
        var proof = new CustomLoopExpiredOperationProof(CustomLoopExpiredOperationProof.CurrentSchemaVersion, CustomLoopReceiptArtifactClass.LifecycleControlReceipt, completed.OperationId, completed.RequestHash, Convert.ToHexString(SHA256.HashData(receiptBytes)).ToLowerInvariant(), completed.UpdatedAtUtc, completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration);
        var ledger = new CustomLoopReceiptProofLedger(CustomLoopReceiptProofLedger.CurrentSchemaVersion, 1, proof.ExpiredAtUtc, null, ImmutableArray<CustomLoopDefinitionLineageProof>.Empty, [proof]);
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath, CustomLoopReceiptRetentionContractCodec.SerializeProofLedger(ledger));

        var lookup = await Assert.ThrowsAsync<FormatException>(() => store.LookupOperationAsync(completed.OperationId));
        var replay = await Assert.ThrowsAsync<FormatException>(() => store.BeginAsync(pending));

        Assert.Contains("contradictory raw and compact expiry evidence", lookup.Message, StringComparison.Ordinal);
        Assert.Contains("contradictory raw and compact expiry evidence", replay.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(receiptPath));
    }

    [Fact]
    public async Task Mutation_reclaims_only_recognized_abandoned_temp_and_orphan_owner_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopControlOperationsPath);
        var tempPath = Path.Combine(paths.CustomLoopControlOperationsPath, $".abandoned-receipt.json.{Guid.NewGuid():N}.tmp");
        var ownerPath = Path.Combine(paths.CustomLoopControlOperationsPath, ".abandoned-owner.owner.lock");
        Directory.CreateDirectory(paths.CustomLoopControlReceiptCleanupPath);
        var cleanupTempPath = Path.Combine(paths.CustomLoopControlReceiptCleanupPath, $".active.json.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        var proofTempPath = Path.Combine(paths.CustomLoopReceiptRetentionPath, $".proof-ledger.json.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tempPath, "partial");
        await File.WriteAllTextAsync(ownerPath, string.Empty);
        await File.WriteAllTextAsync(cleanupTempPath, "partial");
        await File.WriteAllTextAsync(proofTempPath, "partial");
        var store = new CustomLoopControlOperationStore(paths);

        var created = await store.BeginAsync(Pending("control-after-internal-recovery", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);

        Assert.Equal(CustomLoopControlOperationStoreStatus.Created, created.Status);
        Assert.False(File.Exists(tempPath));
        Assert.False(File.Exists(ownerPath));
        Assert.False(File.Exists(cleanupTempPath));
        Assert.False(File.Exists(proofTempPath));

        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopControlOperationsPath, "unexpected.bin"), "unknown");
        var failure = await Assert.ThrowsAsync<FormatException>(() => store.BeginAsync(Pending("control-after-unknown-artifact", AuditSchema.Actors.Web)));
        Assert.Contains("unrecognized artifact", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspection_fails_closed_without_removing_an_abandoned_temp_outside_mutation_ownership()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopControlReceiptCleanupPath);
        var tempPath = Path.Combine(paths.CustomLoopControlReceiptCleanupPath, $".active.json.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tempPath, "partial");

        var posture = await new CustomLoopControlOperationStore(paths).InspectAsync();

        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, posture.CleanupBlockReason);
        Assert.Equal(1, posture.Categories.Single(item => item.Category == CustomLoopReceiptArtifactCategory.Corrupt).ArtifactCount);
        Assert.True(File.Exists(tempPath));
    }

    [Fact]
    public async Task Cleanup_reports_corrupt_evidence_without_deleting_any_receipt()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopControlOperationsPath);
        var corruptPath = Path.Combine(paths.CustomLoopControlOperationsPath, "control-corrupt-retention.json");
        await File.WriteAllTextAsync(corruptPath, "{ malformed");
        var now = _timestamp + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var result = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), new MutableTimeProvider(now)).CleanupAsync(CleanupRequest("cleanup-corrupt", now));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, result.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, result.BlockReason);
        Assert.True(File.Exists(corruptPath));
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

    private static CustomLoopControlOperation Complete(CustomLoopControlOperation operation, DateTimeOffset completedAtUtc)
    {
        return operation with
        {
            UpdatedAtUtc = completedAtUtc,
            State = CustomLoopControlOperationState.Complete,
            Outcome = CustomLoopControlStatus.PauseRequested,
            ResultLifecycleVersion = 3,
            ResultRunStatus = CustomLoopRunStatus.PauseRequested,
            OutcomeAuditRecorded = true,
            Detail = "Pause was durably requested."
        };
    }

    private static CustomLoopReceiptCleanupRequest CleanupRequest(string operationId, DateTimeOffset requestedAtUtc)
    {
        return new CustomLoopReceiptCleanupRequest(
            CustomLoopReceiptCleanupRequest.CurrentSchemaVersion,
            CustomLoopReceiptArtifactClass.LifecycleControlReceipt,
            operationId,
            AuditSchema.Actors.Web,
            "web",
            requestedAtUtc,
            CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(requestedAtUtc),
            4,
            64 * 1024);
    }

    private static CustomLoopReceiptCleanupJournal ReadCleanupJournal(string path)
    {
        return CustomLoopReceiptRetentionContractCodec.DeserializeCleanupJournal(File.ReadAllBytes(path));
    }

    private static bool IsRemovalWriteAheadOfJournal(string journalPath, IReadOnlyCollection<string> receiptPaths, int crashAfterRemovalCount)
    {
        if (!File.Exists(journalPath))
        {
            return false;
        }

        var journal = ReadCleanupJournal(journalPath);
        var missingCount = receiptPaths.Count(path => !File.Exists(path));
        return journal.Stage == CustomLoopReceiptCleanupStage.ProofLedgerWritten
            && missingCount >= crashAfterRemovalCount
            && journal.RemovedArtifactCount < missingCount;
    }

    private static bool IsReconstructedProgressBeforeNextStage(string journalPath, IReadOnlyCollection<string> receiptPaths, int reconstructedCount)
    {
        if (!File.Exists(journalPath))
        {
            return false;
        }

        var journal = ReadCleanupJournal(journalPath);
        var missingCount = receiptPaths.Count(path => !File.Exists(path));
        var expectedMissingCount = reconstructedCount == receiptPaths.Count ? reconstructedCount : reconstructedCount + 1;
        return journal.Stage == CustomLoopReceiptCleanupStage.ProofLedgerWritten
            && journal.RemovedArtifactCount == reconstructedCount
            && missingCount == expectedMissingCount;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class BoundaryCrashTimeProvider(DateTimeOffset utcNow, Func<bool> shouldCrash) : TimeProvider
    {
        private bool _hasCrashed;

        public override DateTimeOffset GetUtcNow()
        {
            if (!_hasCrashed && shouldCrash())
            {
                _hasCrashed = true;
                throw new IOException("Injected crash after an irreversible retention write and before its journal transition.");
            }

            return utcNow;
        }
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
    }

    private sealed class ThrowingAuditLog : IAuditLog
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) => throw new IOException("Injected audit failure.");

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>([]);
    }

    private sealed class RecordThenThrowOnceAuditLog : IAuditLog
    {
        private bool _hasThrown;

        public List<AuditEvent> Events { get; } = [];

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            if (!_hasThrown)
            {
                _hasThrown = true;
                throw new IOException("Injected crash boundary after durable audit append.");
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
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
