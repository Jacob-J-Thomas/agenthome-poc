using EmbodySense.Core.Application.Loops.Models;
using System.Diagnostics;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Tests.Verification;
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
            var recoveryWait = Stopwatch.StartNew();
            CustomLoopControlOperationStoreResult recovered;
            do
            {
                recovered = await new CustomLoopControlOperationStore(new WorkspacePaths(workspace.RootPath)).BeginAsync(pending);
                if (recovered.Lease is not null)
                {
                    break;
                }

                Assert.Equal(CustomLoopControlOperationStoreStatus.OwnershipUnproven, recovered.Status);
                Assert.Equal(process.Id, recovered.Operation!.OwnerProcessId);
                Assert.Equal(liveRetry.Operation.OwnerGenerationId, recovered.Operation.OwnerGenerationId);
                Assert.True(
                    recoveryWait.Elapsed < TimeSpan.FromSeconds(5),
                    "The terminated control-operation owner did not release its cross-process lock within the bounded recovery window.");
                await Task.Delay(10);
            }
            while (true);
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
        var cleanup = await store.CleanupAsync(CleanupCommand("cleanup-expired"));
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
        var unavailable = await initial.CleanupAsync(CleanupCommand(request.OperationId));
        Assert.Equal(CustomLoopReceiptCleanupStatus.AuditUnavailable, unavailable.Status);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
        Assert.Equal(CustomLoopReceiptCleanupStage.IntentAuditStarted, unavailable.Journal!.Stage);

        time.UtcNow += CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time).CleanupAsync(CleanupCommand(request.OperationId));

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
        var interrupted = await initial.CleanupAsync(CleanupCommand("cleanup-stale-owner"));
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
        var interrupted = await initial.CleanupAsync(CleanupCommand(request.OperationId));

        Assert.Equal(CustomLoopReceiptCleanupStatus.AuditUnavailable, interrupted.Status);
        Assert.Equal(CustomLoopReceiptCleanupStage.IntentAuditStarted, interrupted.Journal!.Stage);
        Assert.Single(audit.Events, item => item.Action == AuditSchema.Actions.LoopControlReceiptRetentionIntent);

        time.UtcNow += CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await new CustomLoopControlOperationStore(paths, audit, time).CleanupAsync(CleanupCommand(request.OperationId));

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
        var interrupted = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), crashTime).CleanupAsync(CleanupCommand(request.OperationId));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, interrupted.Status);
        Assert.True(File.Exists(receiptPath));
        Assert.Equal(CustomLoopReceiptCleanupStage.IntentAuditRecorded, ReadCleanupJournal(journalPath).Stage);
        var ledger = CustomLoopReceiptRetentionContractCodec.DeserializeProofLedger(await File.ReadAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath));
        Assert.Equal(completed.UpdatedAtUtc, Assert.Single(ledger.ExpiredOperations).CompletedAtUtc);

        time.UtcNow = replayExpiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time).CleanupAsync(CleanupCommand(request.OperationId));

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
        var interrupted = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), crashTime).CleanupAsync(CleanupCommand(request.OperationId));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, interrupted.Status);
        Assert.Equal(crashAfterRemovalCount, receiptPaths.Count(path => !File.Exists(path)));
        var interruptedJournal = ReadCleanupJournal(journalPath);
        Assert.Equal(CustomLoopReceiptCleanupStage.ProofLedgerWritten, interruptedJournal.Stage);
        Assert.Equal(crashAfterRemovalCount - 1, interruptedJournal.RemovedArtifactCount);

        var firstRecoveryAtUtc = replayExpiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var reconstructionCrashTime = new BoundaryCrashTimeProvider(firstRecoveryAtUtc, () => IsReconstructedProgressBeforeNextStage(journalPath, receiptPaths, crashAfterRemovalCount));
        var reconstructionInterrupted = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), reconstructionCrashTime).CleanupAsync(CleanupCommand(request.OperationId));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, reconstructionInterrupted.Status);
        var reconstructedJournal = ReadCleanupJournal(journalPath);
        Assert.Equal(CustomLoopReceiptCleanupStage.ProofLedgerWritten, reconstructedJournal.Stage);
        Assert.Equal(crashAfterRemovalCount, reconstructedJournal.RemovedArtifactCount);
        Assert.Equal(reconstructedJournal.Candidates.OrderBy(item => item.ArtifactId, StringComparer.Ordinal).Take(crashAfterRemovalCount).Sum(item => item.ArtifactUtf8Bytes), reconstructedJournal.RemovedArtifactUtf8Bytes);

        var finalRecoveryAtUtc = firstRecoveryAtUtc + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), new MutableTimeProvider(finalRecoveryAtUtc)).CleanupAsync(CleanupCommand(request.OperationId));

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
    public async Task Cleanup_derives_retention_time_from_the_trusted_store_clock()
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
        var audit = new RecordingAuditLog();
        var store = new CustomLoopControlOperationStore(paths, audit, new MutableTimeProvider(trustedNow));
        var command = CleanupCommand("cleanup-trusted-time");
        var result = await store.CleanupAsync(command);
        var changed = await store.CleanupAsync(command with { Surface = "cli" });

        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, result.Status);
        Assert.Equal(trustedNow, result.Journal!.Request.RequestedAtUtc);
        Assert.Equal(CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(trustedNow), result.Journal.Request.ReplayCutoffUtc);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Invalid, changed.Status);
        Assert.Equal(result.Journal, changed.Journal);
        Assert.Equal(CustomLoopReceiptOperationLookupStatus.Exact, (await store.LookupOperationAsync(completed.OperationId)).Status);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
        Assert.False(File.Exists(paths.CustomLoopReceiptProofLedgerPath));
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Cleanup_replays_the_same_timestamp_free_command_after_time_advances()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var completedAtUtc = _timestamp;
        var time = new MutableTimeProvider(completedAtUtc);
        var store = new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time);
        var created = await store.BeginAsync(Pending("control-cleanup-replay", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, completedAtUtc);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        time.UtcNow = completedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var command = CleanupCommand("cleanup-command-replay");
        var first = await store.CleanupAsync(command);
        var persistedRequestedAtUtc = first.Journal!.Request.RequestedAtUtc;

        time.UtcNow += TimeSpan.FromDays(2);
        var replay = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time).CleanupAsync(command);

        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, first.Status);
        Assert.False(first.IsReplay);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Replayed, replay.Status);
        Assert.True(replay.IsReplay);
        Assert.Equal(persistedRequestedAtUtc, replay.Journal!.Request.RequestedAtUtc);
    }

    [Fact]
    public async Task Completed_cleanup_identity_survives_journal_rotation_and_cannot_prune_later_receipts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var store = new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time);
        var firstCommand = CleanupCommand("cleanup-history-a");

        var first = await store.CleanupAsync(firstCommand);
        time.UtcNow += TimeSpan.FromMinutes(1);
        var second = await store.CleanupAsync(CleanupCommand("cleanup-history-b"));

        var created = await store.BeginAsync(Pending("control-after-cleanup-a", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, time.UtcNow);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();
        time.UtcNow += CustomLoopReceiptRetentionPolicy.ExactReplayDuration;

        var delayedReplay = await store.CleanupAsync(firstCommand);
        var changedReuse = await store.CleanupAsync(firstCommand with { Surface = "cli" });
        var posture = await store.InspectAsync();

        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, first.Status);
        Assert.False(first.IsReplay);
        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, second.Status);
        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, delayedReplay.Status);
        Assert.True(delayedReplay.IsReplay);
        Assert.Equal(first.Journal, delayedReplay.Journal);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Invalid, changedReuse.Status);
        Assert.Equal(1, posture.CompletedCleanupOperationCount);
        Assert.True(posture.CompletedCleanupHistoryUtf8Bytes > 0);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopLifecycleControlReceiptCleanupHistoryPath, firstCommand.OperationId + ".json")));
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
        var proof = new CustomLoopExpiredOperationProof(CustomLoopExpiredOperationProof.CurrentSchemaVersion, CustomLoopReceiptArtifactClass.LifecycleControlReceipt, null, null, null, completed.OperationId, completed.RequestHash, receiptHash, completedAtUtc, replayExpiry);
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
        var recovered = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time).CleanupAsync(CleanupCommand(request.OperationId));

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
        var proof = new CustomLoopExpiredOperationProof(CustomLoopExpiredOperationProof.CurrentSchemaVersion, CustomLoopReceiptArtifactClass.LifecycleControlReceipt, null, null, null, completed.OperationId, completed.RequestHash, receiptHash, completedAtUtc, replayExpiry);
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
        var recovered = await new CustomLoopControlOperationStore(paths, recoveredAudit, time).CleanupAsync(CleanupCommand(request.OperationId));

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
        var proof = new CustomLoopExpiredOperationProof(CustomLoopExpiredOperationProof.CurrentSchemaVersion, CustomLoopReceiptArtifactClass.LifecycleControlReceipt, null, null, null, completed.OperationId, completed.RequestHash, Convert.ToHexString(SHA256.HashData(receiptBytes)).ToLowerInvariant(), completed.UpdatedAtUtc, completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration);
        var ledger = new CustomLoopReceiptProofLedger(CustomLoopReceiptProofLedger.CurrentSchemaVersion, 1, proof.ExpiredAtUtc, null, ImmutableArray<CustomLoopDefinitionLineageProof>.Empty, [proof]);
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath, CustomLoopReceiptRetentionContractCodec.SerializeProofLedger(ledger));

        var lookup = await Assert.ThrowsAsync<FormatException>(() => store.LookupOperationAsync(completed.OperationId));
        var replay = await Assert.ThrowsAsync<FormatException>(() => store.BeginAsync(pending));
        var cleanup = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), new MutableTimeProvider(proof.ExpiredAtUtc)).CleanupAsync(CleanupCommand("cleanup-contradictory-proof"));

        Assert.Contains("contradictory raw and compact expiry evidence", lookup.Message, StringComparison.Ordinal);
        Assert.Contains("contradictory raw and compact expiry evidence", replay.Message, StringComparison.Ordinal);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Degraded, cleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.AmbiguousEvidence, cleanup.BlockReason);
        Assert.True(File.Exists(receiptPath));
    }

    [Fact]
    public async Task New_admission_rejects_any_preexisting_raw_and_compact_proof_contradiction_before_creating_a_receipt_or_owner_lease()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopControlOperationStore(paths, timeProvider: new MutableTimeProvider(_timestamp));
        var existing = await store.BeginAsync(Pending("control-admission-contradictory-existing", AuditSchema.Actors.Web));
        using var existingLease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(existing.Lease);
        var completed = Complete(existing.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);

        var receiptPath = Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json");
        var receiptBytes = await File.ReadAllBytesAsync(receiptPath);
        var proof = new CustomLoopExpiredOperationProof(
            CustomLoopExpiredOperationProof.CurrentSchemaVersion,
            CustomLoopReceiptArtifactClass.LifecycleControlReceipt,
            null,
            null,
            null,
            completed.OperationId,
            completed.RequestHash,
            Convert.ToHexString(SHA256.HashData(receiptBytes)).ToLowerInvariant(),
            completed.UpdatedAtUtc,
            completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration);
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath, CustomLoopReceiptRetentionContractCodec.SerializeProofLedger(new CustomLoopReceiptProofLedger(
            CustomLoopReceiptProofLedger.CurrentSchemaVersion,
            1,
            proof.ExpiredAtUtc,
            null,
            [],
            [proof])));

        var exception = await Assert.ThrowsAsync<FormatException>(() => store.BeginAsync(Pending("control-admission-after-contradiction", AuditSchema.Actors.Web)));

        Assert.Contains("contradictory raw and compact expiry evidence", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(receiptPath));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, "control-admission-after-contradiction.json")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, ".control-admission-after-contradiction.owner.lock")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlReceiptCleanupPath, "active.json")));
    }

    [Fact]
    public async Task Aggregate_raw_receipt_ceiling_fails_during_inventory_preflight_before_the_first_artifact_can_be_parsed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopControlOperationsPath);
        for (var index = 0; index <= 2_048; index++)
        {
            await using var file = new FileStream(Path.Combine(paths.CustomLoopControlOperationsPath, $"control-aggregate-{index:D5}.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            file.SetLength(64 * 1024);
        }

        var lockedPath = Path.Combine(paths.CustomLoopControlOperationsPath, "control-aggregate-00000.json");
        using var unreadableFirstArtifact = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var store = new CustomLoopControlOperationStore(paths);

        var exception = await Assert.ThrowsAsync<FormatException>(() => store.BeginAsync(Pending("control-after-aggregate-overflow", AuditSchema.Actors.Web)));

        Assert.Contains("aggregate UTF-8 byte ceiling", exception.Message, StringComparison.Ordinal);
        Assert.Equal(64 * 1024, unreadableFirstArtifact.Length);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, "control-after-aggregate-overflow.json")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, ".control-after-aggregate-overflow.owner.lock")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlReceiptCleanupPath, "active.json")));
    }

    [Fact]
    public async Task Inspection_leaves_cleanup_history_temps_untouched_while_another_process_owns_the_shared_retention_mutation_lock()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopLifecycleControlReceiptCleanupHistoryPath);
        var tempPath = Path.Combine(paths.CustomLoopLifecycleControlReceiptCleanupHistoryPath, $".cleanup-history-temp.json.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tempPath, "partial");
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);

        var lockPath = Path.Combine(paths.CustomLoopReceiptRetentionPath, ".custom-loop-mutations.lock");
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var blocked = await new CustomLoopControlOperationStore(paths).InspectAsync();

            Assert.Equal(CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, blocked.CleanupBlockReason);
            Assert.True(File.Exists(tempPath));
        }

        var recovered = await new CustomLoopControlOperationStore(paths).InspectAsync();

        Assert.Equal(CustomLoopReceiptCleanupBlockReason.None, recovered.CleanupBlockReason);
        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public async Task Operation_lookup_fails_closed_before_reading_raw_or_compact_evidence_while_cleanup_owns_the_shared_retention_lock()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        var lockPath = Path.Combine(paths.CustomLoopReceiptRetentionPath, ".custom-loop-mutations.lock");
        using var retentionLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new CustomLoopControlOperationStore(paths).LookupOperationAsync("control-lookup-under-retention-lock"));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Contains("locked by another process", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_receipt_read_fails_closed_before_reading_raw_or_compact_evidence_while_cleanup_owns_the_shared_retention_lock()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        var lockPath = Path.Combine(paths.CustomLoopReceiptRetentionPath, ".custom-loop-mutations.lock");
        using var retentionLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new CustomLoopControlOperationStore(paths).GetAsync("control-get-under-retention-lock"));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Contains("locked by another process", exception.Message, StringComparison.Ordinal);
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
        var result = await new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), new MutableTimeProvider(now)).CleanupAsync(CleanupCommand("cleanup-corrupt"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, result.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, result.BlockReason);
        Assert.True(File.Exists(corruptPath));
    }

    [Fact]
    public async Task Cleanup_without_a_governed_audit_sink_preserves_expired_receipts_and_reports_the_live_retention_horizon()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var store = new CustomLoopControlOperationStore(paths, timeProvider: time);
        var created = await store.BeginAsync(Pending("control-no-cleanup-audit", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        time.UtcNow += TimeSpan.FromDays(1);
        var livePosture = await store.InspectAsync();
        time.UtcNow = completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var cleanup = await store.CleanupAsync(CleanupCommand("cleanup-no-audit-sink"));

        Assert.Equal(completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration, livePosture.OldestExactReplayExpiresAtUtc);
        Assert.Equal(livePosture.OldestExactReplayExpiresAtUtc, livePosture.NewestExactReplayExpiresAtUtc);
        Assert.Equal(CustomLoopReceiptCleanupStatus.AuditUnavailable, cleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupStage.IntentPersisted, cleanup.Journal!.Stage);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
        Assert.False(File.Exists(paths.CustomLoopReceiptProofLedgerPath));
    }

    [Fact]
    public async Task Stale_unaudited_cleanup_intent_is_degraded_before_a_distinct_lifecycle_operation_is_admitted()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var store = new CustomLoopControlOperationStore(paths, timeProvider: time);
        var created = await store.BeginAsync(Pending("control-stale-intent", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var expiry = completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-stale-intent", expiry);
        var candidate = await CreateCandidateAsync(paths, completed, expiry);
        await WriteCleanupJournalAsync(paths, new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-stale-intent",
            Environment.ProcessId,
            expiry,
            CustomLoopReceiptCleanupStage.IntentPersisted,
            CustomLoopReceiptCleanupOutcome.Unknown,
            expiry,
            [candidate],
            null,
            0,
            0,
            "Crash before the intent audit could begin."));

        time.UtcNow = expiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var admitted = await store.BeginAsync(Pending("control-after-stale-intent", AuditSchema.Actors.Web));
        using var admittedLease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(admitted.Lease);
        var journal = ReadCleanupJournal(Path.Combine(paths.CustomLoopControlReceiptCleanupPath, "active.json"));

        Assert.Equal(CustomLoopControlOperationStoreStatus.Created, admitted.Status);
        Assert.Equal(CustomLoopReceiptCleanupStage.Degraded, journal.Stage);
        Assert.Equal(CustomLoopReceiptCleanupOutcome.Degraded, journal.Outcome);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
        Assert.False(File.Exists(paths.CustomLoopReceiptProofLedgerPath));
    }

    [Fact]
    public async Task Stale_cleanup_preserves_raw_receipts_when_an_existing_expiry_proof_disagrees_with_the_selected_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var store = new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time);
        var created = await store.BeginAsync(Pending("control-ambiguous-proof", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var expiry = completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-ambiguous-proof", expiry);
        var candidate = await CreateCandidateAsync(paths, completed, expiry);
        var conflictingProof = candidate.ExpiredOperationProof! with { OutcomeHash = new string('0', CustomLoopLimits.Sha256HexCharacters) };
        var ledger = new CustomLoopReceiptProofLedger(CustomLoopReceiptProofLedger.CurrentSchemaVersion, 1, expiry, null, ImmutableArray<CustomLoopDefinitionLineageProof>.Empty, [conflictingProof]);
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath, CustomLoopReceiptRetentionContractCodec.SerializeProofLedger(ledger));
        await WriteCleanupJournalAsync(paths, new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-ambiguous-proof",
            Environment.ProcessId,
            expiry,
            CustomLoopReceiptCleanupStage.IntentAuditRecorded,
            CustomLoopReceiptCleanupOutcome.Unknown,
            expiry,
            [candidate],
            null,
            0,
            0,
            "Crash after intent audit confirmation and before proof persistence."));

        time.UtcNow = expiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await store.CleanupAsync(CleanupCommand(request.OperationId));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Degraded, recovered.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.AmbiguousEvidence, recovered.BlockReason);
        Assert.Equal(CustomLoopReceiptCleanupStage.Degraded, recovered.Journal!.Stage);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
    }

    [Fact]
    public async Task Stale_post_proof_cleanup_fails_closed_when_the_durable_ledger_no_longer_matches_its_journal()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var store = new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time);
        var created = await store.BeginAsync(Pending("control-proof-journal-conflict", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var expiry = completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-proof-journal-conflict", expiry);
        var candidate = await CreateCandidateAsync(paths, completed, expiry);
        var ledger = new CustomLoopReceiptProofLedger(CustomLoopReceiptProofLedger.CurrentSchemaVersion, 1, expiry, null, ImmutableArray<CustomLoopDefinitionLineageProof>.Empty, [candidate.ExpiredOperationProof!]);
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath, CustomLoopReceiptRetentionContractCodec.SerializeProofLedger(ledger));
        await WriteCleanupJournalAsync(paths, new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-proof-journal-conflict",
            Environment.ProcessId,
            expiry,
            CustomLoopReceiptCleanupStage.ProofLedgerWritten,
            CustomLoopReceiptCleanupOutcome.Unknown,
            expiry,
            [candidate],
            new string('0', CustomLoopLimits.Sha256HexCharacters),
            0,
            0,
            "Crash after a proof-ledger write with a conflicting journal hash."));

        time.UtcNow = expiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await store.CleanupAsync(CleanupCommand(request.OperationId));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, recovered.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, recovered.BlockReason);
        Assert.Equal(CustomLoopReceiptCleanupStage.Degraded, recovered.Journal!.Stage);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
    }

    [Fact]
    public async Task Cleanup_retains_an_auditable_warning_when_the_outcome_audit_fails_after_raw_removal()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var audit = new ThrowOnSecondAppendAuditLog();
        var store = new CustomLoopControlOperationStore(paths, audit, time);
        var created = await store.BeginAsync(Pending("control-outcome-audit-failure", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        time.UtcNow = completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var cleanup = await store.CleanupAsync(CleanupCommand("cleanup-outcome-audit-failure"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning, cleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupStage.CommittedWithAuditWarning, cleanup.Journal!.Stage);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.AuditUnavailable, cleanup.BlockReason);
        Assert.Single(audit.Events, item => item.Action == AuditSchema.Actions.LoopControlReceiptRetentionIntent);
        Assert.Single(audit.Events, item => item.Action == AuditSchema.Actions.LoopControlReceiptRetentionOutcome);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
        Assert.Equal(CustomLoopReceiptOperationLookupStatus.Expired, (await store.LookupOperationAsync(completed.OperationId)).Status);
    }

    [Fact]
    public async Task Inspection_and_cleanup_report_an_active_foreign_cleanup_owner_without_admitting_a_second_cleanup()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var store = new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time);
        var created = await store.BeginAsync(Pending("control-active-cleanup-owner", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var expiry = completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-active-owner", expiry);
        var candidate = await CreateCandidateAsync(paths, completed, expiry);
        await WriteCleanupJournalAsync(paths, new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-active",
            Environment.ProcessId,
            _timestamp,
            CustomLoopReceiptCleanupStage.IntentPersisted,
            CustomLoopReceiptCleanupOutcome.Unknown,
            _timestamp,
            [candidate],
            null,
            0,
            0,
            "Another process owns the active cleanup intent."));

        var posture = await store.InspectAsync();
        var second = await store.CleanupAsync(CleanupCommand("cleanup-different-owner"));
        var blockedAdmission = await store.BeginAsync(Pending("control-blocked-by-cleanup-owner", AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, posture.CleanupBlockReason);
        Assert.Equal(CustomLoopReceiptCleanupStatus.OperationInProgress, second.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, second.BlockReason);
        Assert.Equal(CustomLoopControlOperationStoreStatus.OwnershipUnproven, blockedAdmission.Status);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
    }

    [Fact]
    public async Task Cleanup_rejects_a_valid_command_for_a_different_receipt_artifact_class()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var command = new CustomLoopReceiptCleanupCommand(
            CustomLoopReceiptCleanupCommand.CurrentSchemaVersion,
            CustomLoopReceiptArtifactClass.DefinitionMutationReceipt,
            "cleanup-wrong-artifact-class",
            AuditSchema.Actors.Web,
            "web",
            4,
            64 * 1024);

        var result = await new CustomLoopControlOperationStore(paths).CleanupAsync(command);

        Assert.Equal(CustomLoopReceiptCleanupStatus.Invalid, result.Status);
        Assert.Contains("different receipt artifact class", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Corrupt_receipt_encoding_and_missing_owner_metadata_remain_distinct_fail_closed_read_errors()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopControlOperationStore(paths, timeProvider: new MutableTimeProvider(_timestamp));
        var created = await store.BeginAsync(Pending("control-corrupt-read", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var receiptPath = Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json");
        await File.WriteAllTextAsync(receiptPath, "{\"kind\":99}");
        var invalidEnum = await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(completed.OperationId));

        var withoutOwner = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(paths.CustomLoopControlOperationsPath, "control-corrupt-read.json")))!.AsObject();
        withoutOwner["kind"] = "pause";
        withoutOwner["schemaVersion"] = completed.SchemaVersion;
        withoutOwner["operationId"] = completed.OperationId;
        withoutOwner["requestHash"] = completed.RequestHash;
        withoutOwner["runId"] = completed.RunId;
        withoutOwner["expectedLifecycleVersion"] = completed.ExpectedLifecycleVersion;
        withoutOwner["actor"] = completed.Actor;
        withoutOwner["createdAtUtc"] = completed.CreatedAtUtc;
        withoutOwner["updatedAtUtc"] = completed.UpdatedAtUtc;
        withoutOwner["state"] = "complete";
        withoutOwner["outcome"] = "pauseRequested";
        withoutOwner["resultLifecycleVersion"] = completed.ResultLifecycleVersion;
        withoutOwner["resultRunStatus"] = "pauseRequested";
        withoutOwner["outcomeAuditRecorded"] = true;
        withoutOwner["detail"] = completed.Detail;
        withoutOwner.Remove("ownerGenerationId");
        withoutOwner.Remove("ownerProcessId");
        withoutOwner.Remove("ownerAcquiredAtUtc");
        await File.WriteAllTextAsync(receiptPath, withoutOwner.ToJsonString());
        var missingOwner = await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(completed.OperationId));

        Assert.Contains("invalid JSON", invalidEnum.Message, StringComparison.Ordinal);
        Assert.Contains("missing ownership metadata", missingOwner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_proof_cleanup_stops_when_a_selected_receipt_has_live_execution_ownership()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var store = new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time);
        var created = await store.BeginAsync(Pending("control-post-proof-owner", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var expiry = completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-post-proof-owner", expiry);
        var candidate = await CreateCandidateAsync(paths, completed, expiry);
        var ledger = new CustomLoopReceiptProofLedger(CustomLoopReceiptProofLedger.CurrentSchemaVersion, 1, expiry, null, ImmutableArray<CustomLoopDefinitionLineageProof>.Empty, [candidate.ExpiredOperationProof!]);
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath, CustomLoopReceiptRetentionContractCodec.SerializeProofLedger(ledger));
        await WriteCleanupJournalAsync(paths, new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-post-proof",
            Environment.ProcessId,
            expiry,
            CustomLoopReceiptCleanupStage.ProofLedgerWritten,
            CustomLoopReceiptCleanupOutcome.Unknown,
            expiry,
            [candidate],
            CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(ledger),
            0,
            0,
            "Crash after proof persistence while execution ownership remained active."));
        var ownerPath = Path.Combine(paths.CustomLoopControlOperationsPath, $".{completed.OperationId}.owner.lock");
        using var activeOwner = new FileStream(ownerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        time.UtcNow = expiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await store.CleanupAsync(CleanupCommand(request.OperationId));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Degraded, recovered.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, recovered.BlockReason);
        Assert.Equal(CustomLoopReceiptCleanupStage.Degraded, recovered.Journal!.Stage);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
    }

    [Fact]
    public async Task Post_removal_recovery_without_an_audit_sink_records_a_terminal_audit_warning_without_repeating_removal()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var store = new CustomLoopControlOperationStore(paths, timeProvider: time);
        var created = await store.BeginAsync(Pending("control-post-removal-no-audit", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var expiry = completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-post-removal-no-audit", expiry);
        var candidate = await CreateCandidateAsync(paths, completed, expiry);
        var ledger = new CustomLoopReceiptProofLedger(CustomLoopReceiptProofLedger.CurrentSchemaVersion, 1, expiry, null, ImmutableArray<CustomLoopDefinitionLineageProof>.Empty, [candidate.ExpiredOperationProof!]);
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath, CustomLoopReceiptRetentionContractCodec.SerializeProofLedger(ledger));
        File.Delete(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json"));
        await WriteCleanupJournalAsync(paths, new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-post-removal",
            Environment.ProcessId,
            expiry,
            CustomLoopReceiptCleanupStage.ArtifactsRemoved,
            CustomLoopReceiptCleanupOutcome.Unknown,
            expiry,
            [candidate],
            CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(ledger),
            1,
            candidate.ArtifactUtf8Bytes,
            "Crash after raw removal and before the outcome audit."));

        time.UtcNow = expiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await store.CleanupAsync(CleanupCommand(request.OperationId));

        Assert.Equal(CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning, recovered.Status);
        Assert.Equal(CustomLoopReceiptCleanupStage.CommittedWithAuditWarning, recovered.Journal!.Stage);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.AuditUnavailable, recovered.BlockReason);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
    }

    [Fact]
    public async Task Cleanup_rejects_an_invalid_command_and_reports_an_external_mutation_lease_as_in_progress()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopControlOperationStore(paths);
        var invalid = CleanupCommand("not a canonical cleanup id");
        var invalidResult = await store.CleanupAsync(invalid);

        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        using var lockOwner = new FileStream(Path.Combine(paths.CustomLoopReceiptRetentionPath, ".custom-loop-mutations.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var lockedResult = await store.CleanupAsync(CleanupCommand("cleanup-held-mutation-lease"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Invalid, invalidResult.Status);
        Assert.Equal(CustomLoopReceiptCleanupStatus.OperationInProgress, lockedResult.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, lockedResult.BlockReason);
    }

    [Fact]
    public async Task Stale_started_intent_with_unconfirmed_audit_evidence_is_degraded_without_repeating_the_append()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var store = new CustomLoopControlOperationStore(paths, new ThrowingAuditLog(), time);
        var created = await store.BeginAsync(Pending("control-started-intent", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var expiry = completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-started-intent", expiry);
        var candidate = await CreateCandidateAsync(paths, completed, expiry);
        await WriteCleanupJournalAsync(paths, new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-started-intent",
            Environment.ProcessId,
            expiry,
            CustomLoopReceiptCleanupStage.IntentAuditStarted,
            CustomLoopReceiptCleanupOutcome.Unknown,
            expiry,
            [candidate],
            null,
            0,
            0,
            "Crash after starting an intent audit without durable confirmation."));

        time.UtcNow = expiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await store.CleanupAsync(CleanupCommand(request.OperationId));

        Assert.Equal(CustomLoopReceiptCleanupStatus.AuditUnavailable, recovered.Status);
        Assert.Equal(CustomLoopReceiptCleanupStage.Degraded, recovered.Journal!.Stage);
        Assert.Equal(CustomLoopReceiptCleanupOutcome.AuditUnavailable, recovered.Journal.Outcome);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
    }

    [Fact]
    public async Task Post_proof_recovery_degrades_when_removal_history_is_not_a_canonical_prefix()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var store = new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), time);
        var completed = new List<CustomLoopControlOperation>();
        foreach (var operationId in new[] { "control-prefix-a", "control-prefix-b" })
        {
            var created = await store.BeginAsync(Pending(operationId, AuditSchema.Actors.Web));
            using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
            var terminal = Complete(created.Operation!, _timestamp);
            Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(terminal)).Status);
            lease.Dispose();
            completed.Add(terminal);
        }

        var expiry = _timestamp + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-noncanonical-prefix", expiry);
        var candidates = (await Task.WhenAll(completed.Select(item => CreateCandidateAsync(paths, item, expiry)))).OrderBy(item => item.ArtifactId, StringComparer.Ordinal).ToImmutableArray();
        var ledger = new CustomLoopReceiptProofLedger(CustomLoopReceiptProofLedger.CurrentSchemaVersion, 1, expiry, null, ImmutableArray<CustomLoopDefinitionLineageProof>.Empty, candidates.Select(item => item.ExpiredOperationProof!).ToImmutableArray());
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath, CustomLoopReceiptRetentionContractCodec.SerializeProofLedger(ledger));
        await WriteCleanupJournalAsync(paths, new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-noncanonical-prefix",
            Environment.ProcessId,
            expiry,
            CustomLoopReceiptCleanupStage.ProofLedgerWritten,
            CustomLoopReceiptCleanupOutcome.Unknown,
            expiry,
            candidates,
            CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(ledger),
            0,
            0,
            "Crash after an out-of-order raw receipt disappearance."));
        File.Delete(Path.Combine(paths.CustomLoopControlOperationsPath, "control-prefix-b.json"));

        time.UtcNow = expiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await store.CleanupAsync(CleanupCommand(request.OperationId));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Degraded, recovered.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.AmbiguousEvidence, recovered.BlockReason);
        Assert.Equal(0, recovered.Journal!.RemovedArtifactCount);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, "control-prefix-a.json")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, "control-prefix-b.json")));
    }

    [Theory]
    [InlineData(CustomLoopReceiptCleanupStage.CommittedWithAuditWarning, CustomLoopReceiptCleanupOutcome.AuditUnavailable, CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning, CustomLoopReceiptCleanupBlockReason.AuditUnavailable)]
    [InlineData(CustomLoopReceiptCleanupStage.AbandonedConflict, CustomLoopReceiptCleanupOutcome.Conflict, CustomLoopReceiptCleanupStatus.CleanupConflict, CustomLoopReceiptCleanupBlockReason.CleanupConflict)]
    [InlineData(CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.AuditUnavailable, CustomLoopReceiptCleanupStatus.AuditUnavailable, CustomLoopReceiptCleanupBlockReason.AuditUnavailable)]
    [InlineData(CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Corrupt, CustomLoopReceiptCleanupStatus.Corrupt, CustomLoopReceiptCleanupBlockReason.CorruptEvidence)]
    [InlineData(CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Degraded, CustomLoopReceiptCleanupStatus.Degraded, CustomLoopReceiptCleanupBlockReason.AmbiguousEvidence)]
    public async Task Terminal_cleanup_journal_replay_preserves_its_explicit_failure_classification(CustomLoopReceiptCleanupStage stage, CustomLoopReceiptCleanupOutcome outcome, CustomLoopReceiptCleanupStatus expectedStatus, CustomLoopReceiptCleanupBlockReason expectedBlockReason)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var store = new CustomLoopControlOperationStore(paths, timeProvider: time);
        var created = await store.BeginAsync(Pending("control-terminal-journal", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var expiry = completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-terminal-journal", expiry);
        var candidate = await CreateCandidateAsync(paths, completed, expiry);
        var removedCount = stage == CustomLoopReceiptCleanupStage.CommittedWithAuditWarning ? 1 : 0;
        var removedBytes = removedCount == 1 ? candidate.ArtifactUtf8Bytes : 0;
        await WriteCleanupJournalAsync(paths, new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-terminal-journal",
            Environment.ProcessId,
            expiry,
            stage,
            outcome,
            expiry,
            [candidate],
            stage == CustomLoopReceiptCleanupStage.CommittedWithAuditWarning ? new string('0', CustomLoopLimits.Sha256HexCharacters) : null,
            removedCount,
            removedBytes,
            "A terminal cleanup outcome must replay without changing its classification."));

        var replay = await store.CleanupAsync(CleanupCommand(request.OperationId));

        Assert.Equal(expectedStatus, replay.Status);
        Assert.Equal(expectedBlockReason, replay.BlockReason);
        Assert.Equal(stage, replay.Journal!.Stage);
        Assert.Equal(outcome, replay.Journal.Outcome);
    }

    [Fact]
    public async Task Completion_rejects_a_foreign_owner_generation_without_overwriting_the_pending_receipt()
    {
        using var workspace = new TestWorkspace();
        var time = new MutableTimeProvider(_timestamp);
        var store = new CustomLoopControlOperationStore(new WorkspacePaths(workspace.RootPath), timeProvider: time);
        var created = await store.BeginAsync(Pending("control-foreign-owner", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var pending = Assert.IsType<CustomLoopControlOperation>(created.Operation);

        var conflict = await store.CompleteAsync(Complete(pending, _timestamp) with { OwnerGenerationId = "control-owner-foreign" });

        Assert.Equal(CustomLoopControlOperationStoreStatus.Conflict, conflict.Status);
        Assert.Equal(CustomLoopControlOperationState.Pending, conflict.Operation!.State);
        Assert.Equal(pending.OwnerGenerationId, conflict.Operation.OwnerGenerationId);
    }

    [Fact]
    public async Task Cleanup_fails_closed_when_completed_history_or_the_active_journal_is_not_trusted_control_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopLifecycleControlReceiptCleanupHistoryPath);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopLifecycleControlReceiptCleanupHistoryPath, "not-a-journal.bin"), "unexpected");
        var historyFailure = await new CustomLoopControlOperationStore(paths).CleanupAsync(CleanupCommand("cleanup-corrupt-history"));

        using var journalWorkspace = new TestWorkspace();
        var journalPaths = new WorkspacePaths(journalWorkspace.RootPath);
        var request = new CustomLoopReceiptCleanupRequest(
            CustomLoopReceiptCleanupRequest.CurrentSchemaVersion,
            CustomLoopReceiptArtifactClass.DefinitionMutationReceipt,
            "cleanup-wrong-journal-class",
            AuditSchema.Actors.Web,
            "web",
            _timestamp,
            CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(_timestamp),
            4,
            64 * 1024);
        await WriteCleanupJournalAsync(journalPaths, new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-wrong-class",
            Environment.ProcessId,
            _timestamp,
            CustomLoopReceiptCleanupStage.Completed,
            CustomLoopReceiptCleanupOutcome.NothingEligible,
            _timestamp,
            [],
            null,
            0,
            0,
            "A non-control journal cannot be consumed by control retention."));
        var journalFailure = await new CustomLoopControlOperationStore(journalPaths).CleanupAsync(CleanupCommand("cleanup-read-wrong-class"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, historyFailure.Status);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, journalFailure.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, historyFailure.BlockReason);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, journalFailure.BlockReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_started_intent_preserves_raw_receipts_when_audit_confirmation_is_unavailable_or_unreadable(bool auditReaderThrows)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        IAuditLog? audit = auditReaderThrows ? new ThrowingReadAuditLog() : null;
        var store = new CustomLoopControlOperationStore(paths, audit, time);
        var scenario = auditReaderThrows ? "throw" : "none";
        var created = await store.BeginAsync(Pending($"control-unreadable-audit-{scenario}", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var expiry = completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest($"cleanup-unreadable-audit-{scenario}", expiry);
        var candidate = await CreateCandidateAsync(paths, completed, expiry);
        await WriteCleanupJournalAsync(paths, new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-unreadable-audit",
            Environment.ProcessId,
            expiry,
            CustomLoopReceiptCleanupStage.IntentAuditStarted,
            CustomLoopReceiptCleanupOutcome.Unknown,
            expiry,
            [candidate],
            null,
            0,
            0,
            "Audit confirmation could not be recovered safely."));

        time.UtcNow = expiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await store.CleanupAsync(CleanupCommand(request.OperationId));

        Assert.Equal(CustomLoopReceiptCleanupStatus.AuditUnavailable, recovered.Status);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
    }

    [Fact]
    public async Task Stale_foreign_cleanup_is_recovered_before_a_different_cleanup_operation_can_proceed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var audit = new RecordingAuditLog();
        var store = new CustomLoopControlOperationStore(paths, audit, time);
        var created = await store.BeginAsync(Pending("control-stale-foreign-cleanup", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var expiry = completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var staleRequest = CleanupRequest("cleanup-stale-foreign", expiry);
        var candidate = await CreateCandidateAsync(paths, completed, expiry);
        await WriteCleanupJournalAsync(paths, new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            staleRequest,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(staleRequest),
            "cleanup-owner-stale-foreign",
            Environment.ProcessId,
            expiry,
            CustomLoopReceiptCleanupStage.IntentPersisted,
            CustomLoopReceiptCleanupOutcome.Unknown,
            expiry,
            [candidate],
            null,
            0,
            0,
            "A different cleanup owner expired before it could audit intent."));

        time.UtcNow = expiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var replacement = await store.CleanupAsync(CleanupCommand("cleanup-after-stale-foreign"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, replacement.Status);
        Assert.Single(audit.Events, item => item.Target == staleRequest.OperationId && item.Action == AuditSchema.Actions.LoopControlReceiptRetentionIntent);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
    }

    [Fact]
    public async Task Started_intent_with_matching_audit_fields_but_no_request_hash_is_not_confirmed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var audit = new RecordingAuditLog();
        var store = new CustomLoopControlOperationStore(paths, audit, time);
        var created = await store.BeginAsync(Pending("control-audit-metadata-gap", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        var expiry = completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var request = CleanupRequest("cleanup-audit-metadata-gap", expiry);
        var candidate = await CreateCandidateAsync(paths, completed, expiry);
        audit.Events.Add(AuditEvent.Create(request.Actor, AuditSchema.Actions.LoopControlReceiptRetentionIntent, request.OperationId, AuditSchema.Outcomes.Requested, "An incomplete audit record cannot prove intent.", new Dictionary<string, object?>()));
        await WriteCleanupJournalAsync(paths, new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-audit-metadata-gap",
            Environment.ProcessId,
            expiry,
            CustomLoopReceiptCleanupStage.IntentAuditStarted,
            CustomLoopReceiptCleanupOutcome.Unknown,
            expiry,
            [candidate],
            null,
            0,
            0,
            "Intent audit metadata was incomplete at the crash boundary."));

        time.UtcNow = expiry + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1);
        var recovered = await store.CleanupAsync(CleanupCommand(request.OperationId));

        Assert.Equal(CustomLoopReceiptCleanupStatus.AuditUnavailable, recovered.Status);
        Assert.Equal(CustomLoopReceiptCleanupStage.Degraded, recovered.Journal!.Stage);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json")));
    }

    [Fact]
    public async Task Retention_storage_shape_and_missing_owner_evidence_fail_closed_without_cleanup()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(Path.Combine(paths.CustomLoopControlOperationsPath, "nested"));
        var nestedOperations = await new CustomLoopControlOperationStore(paths).CleanupAsync(CleanupCommand("cleanup-nested-operations"));

        using var cleanupWorkspace = new TestWorkspace();
        var cleanupPaths = new WorkspacePaths(cleanupWorkspace.RootPath);
        Directory.CreateDirectory(Path.Combine(cleanupPaths.CustomLoopControlReceiptCleanupPath, "nested"));
        var nestedCleanup = await new CustomLoopControlOperationStore(cleanupPaths).CleanupAsync(CleanupCommand("cleanup-nested-journal"));

        using var retentionWorkspace = new TestWorkspace();
        var retentionPaths = new WorkspacePaths(retentionWorkspace.RootPath);
        Directory.CreateDirectory(retentionPaths.CustomLoopReceiptRetentionPath);
        await File.WriteAllTextAsync(Path.Combine(retentionPaths.CustomLoopReceiptRetentionPath, ".abandoned-proof.tmp"), "partial");
        var abandonedRetention = await new CustomLoopControlOperationStore(retentionPaths).CleanupAsync(CleanupCommand("cleanup-abandoned-retention-temp"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, nestedOperations.Status);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, nestedCleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, abandonedRetention.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, nestedOperations.BlockReason);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, nestedCleanup.BlockReason);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, abandonedRetention.BlockReason);
    }

    [Fact]
    public async Task Active_or_unrecognized_internal_cleanup_artifacts_fail_closed_before_lifecycle_or_cleanup_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopControlOperationsPath);
        using var orphanOwner = new FileStream(Path.Combine(paths.CustomLoopControlOperationsPath, ".control-active-orphan.owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        var lifecycleFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CustomLoopControlOperationStore(paths).BeginAsync(Pending("control-after-active-orphan", AuditSchema.Actors.Web)));

        using var cleanupWorkspace = new TestWorkspace();
        var cleanupPaths = new WorkspacePaths(cleanupWorkspace.RootPath);
        Directory.CreateDirectory(cleanupPaths.CustomLoopControlReceiptCleanupPath);
        await File.WriteAllTextAsync(Path.Combine(cleanupPaths.CustomLoopControlReceiptCleanupPath, "unrecognized.bin"), "unexpected");
        var cleanupFailure = await new CustomLoopControlOperationStore(cleanupPaths).CleanupAsync(CleanupCommand("cleanup-unrecognized-internal"));

        Assert.Contains("retains active ownership", lifecycleFailure.Message, StringComparison.Ordinal);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, cleanupFailure.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, cleanupFailure.BlockReason);
    }

    [Fact]
    public async Task Max_plus_one_raw_receipts_fail_before_json_reads_or_any_lifecycle_control_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopControlOperationsPath);
        Parallel.For(
            0,
            CustomLoopReceiptRetentionPolicy.MaxLifecycleControlReceiptCount + 1,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) },
            index => File.Create(Path.Combine(paths.CustomLoopControlOperationsPath, $"control-inventory-{index:D5}.json")).Dispose());

        var lockedPath = Path.Combine(paths.CustomLoopControlOperationsPath, "control-inventory-00000.json");
        using var unreadableReceipt = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var store = new CustomLoopControlOperationStore(paths, timeProvider: new MutableTimeProvider(_timestamp));
        var posture = await store.InspectAsync();
        var cleanup = await store.CleanupAsync(CleanupCommand("cleanup-overpopulated-control-inventory"));
        var begin = await Assert.ThrowsAsync<FormatException>(() => store.BeginAsync(Pending("control-after-overpopulated-inventory", AuditSchema.Actors.Web)));
        var ownedPending = Pending("control-complete-overpopulated-inventory", AuditSchema.Actors.Web) with
        {
            OwnerGenerationId = "control-owner-overpopulated-inventory",
            OwnerProcessId = Environment.ProcessId,
            OwnerAcquiredAtUtc = _timestamp
        };
        var complete = await Assert.ThrowsAsync<FormatException>(() => store.CompleteAsync(Complete(ownedPending, _timestamp)));

        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, posture.CleanupBlockReason);
        Assert.Contains("FormatException", posture.Detail, StringComparison.Ordinal);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, cleanup.Status);
        Assert.Contains("bounded inventory ceiling", cleanup.Detail, StringComparison.Ordinal);
        Assert.Contains("bounded inventory ceiling", begin.Message, StringComparison.Ordinal);
        Assert.Contains("bounded inventory ceiling", complete.Message, StringComparison.Ordinal);
        Assert.Equal(CustomLoopReceiptRetentionPolicy.MaxLifecycleControlReceiptCount + 1, Directory.EnumerateFiles(paths.CustomLoopControlOperationsPath, "*", SearchOption.TopDirectoryOnly).Count());
        Assert.Equal(0, unreadableReceipt.Length);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, "control-after-overpopulated-inventory.json")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlOperationsPath, ".control-after-overpopulated-inventory.owner.lock")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopControlReceiptCleanupPath, "active.json")));
    }

    [Fact]
    public async Task Shared_retention_directory_inventory_is_bounded_and_fails_before_lifecycle_control_mutation()
    {
        using var unexpectedWorkspace = new TestWorkspace();
        var unexpectedPaths = new WorkspacePaths(unexpectedWorkspace.RootPath);
        Directory.CreateDirectory(Path.Combine(unexpectedPaths.CustomLoopReceiptRetentionPath, "unexpected-directory"));
        var unexpectedStore = new CustomLoopControlOperationStore(unexpectedPaths);
        var unexpectedPosture = await unexpectedStore.InspectAsync();
        var unexpectedBegin = await Assert.ThrowsAsync<FormatException>(() => unexpectedStore.BeginAsync(Pending("control-after-unexpected-retention-directory", AuditSchema.Actors.Web)));

        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, unexpectedPosture.CleanupBlockReason);
        Assert.Contains("FormatException", unexpectedPosture.Detail, StringComparison.Ordinal);
        Assert.Contains("unrecognized subdirectory", unexpectedBegin.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(unexpectedPaths.CustomLoopControlOperationsPath, "control-after-unexpected-retention-directory.json")));
        Assert.False(File.Exists(Path.Combine(unexpectedPaths.CustomLoopControlOperationsPath, ".control-after-unexpected-retention-directory.owner.lock")));
        Assert.False(File.Exists(Path.Combine(unexpectedPaths.CustomLoopControlReceiptCleanupPath, "active.json")));

        using var overpopulatedWorkspace = new TestWorkspace();
        var overpopulatedPaths = new WorkspacePaths(overpopulatedWorkspace.RootPath);
        for (var index = 0; index < 7; index++)
        {
            Directory.CreateDirectory(Path.Combine(overpopulatedPaths.CustomLoopReceiptRetentionPath, $"unexpected-directory-{index}"));
        }

        var overpopulatedStore = new CustomLoopControlOperationStore(overpopulatedPaths);
        var overpopulatedBegin = await Assert.ThrowsAsync<FormatException>(() => overpopulatedStore.BeginAsync(Pending("control-after-overpopulated-retention-directories", AuditSchema.Actors.Web)));

        Assert.Contains("bounded inventory ceiling", overpopulatedBegin.Message, StringComparison.Ordinal);
        Assert.Equal(7, Directory.EnumerateDirectories(overpopulatedPaths.CustomLoopReceiptRetentionPath, "*", SearchOption.TopDirectoryOnly).Count());
        Assert.False(File.Exists(Path.Combine(overpopulatedPaths.CustomLoopControlOperationsPath, "control-after-overpopulated-retention-directories.json")));
        Assert.False(File.Exists(Path.Combine(overpopulatedPaths.CustomLoopControlOperationsPath, ".control-after-overpopulated-retention-directories.owner.lock")));
        Assert.False(File.Exists(Path.Combine(overpopulatedPaths.CustomLoopControlReceiptCleanupPath, "active.json")));
    }

    [Fact]
    public async Task Max_plus_one_internal_files_fail_before_cleanup_or_shared_retention_reads_and_reclamation()
    {
        using var cleanupWorkspace = new TestWorkspace();
        var cleanupPaths = new WorkspacePaths(cleanupWorkspace.RootPath);
        Directory.CreateDirectory(cleanupPaths.CustomLoopControlReceiptCleanupPath);
        var cleanupFiles = new[]
        {
            Path.Combine(cleanupPaths.CustomLoopControlReceiptCleanupPath, "active.json"),
            Path.Combine(cleanupPaths.CustomLoopControlReceiptCleanupPath, $".active.json.{Guid.NewGuid():N}.tmp"),
            Path.Combine(cleanupPaths.CustomLoopControlReceiptCleanupPath, $".active.json.{Guid.NewGuid():N}.tmp")
        };
        foreach (var path in cleanupFiles)
        {
            await File.WriteAllTextAsync(path, "unreadable");
        }

        var cleanupStore = new CustomLoopControlOperationStore(cleanupPaths);
        var cleanupPosture = await cleanupStore.InspectAsync();
        var cleanup = await cleanupStore.CleanupAsync(CleanupCommand("cleanup-overpopulated-internal-inventory"));

        using var retentionWorkspace = new TestWorkspace();
        var retentionPaths = new WorkspacePaths(retentionWorkspace.RootPath);
        Directory.CreateDirectory(retentionPaths.CustomLoopReceiptRetentionPath);
        var retentionFiles = new[]
        {
            retentionPaths.CustomLoopReceiptProofLedgerPath,
            retentionPaths.CustomLoopDefinitionMutationReceiptCleanupJournalPath,
            retentionPaths.CustomLoopDefinitionTombstoneCleanupJournalPath,
            Path.Combine(retentionPaths.CustomLoopReceiptRetentionPath, ".custom-loop-mutations.lock"),
            Path.Combine(retentionPaths.CustomLoopReceiptRetentionPath, $".proof-ledger.json.{Guid.NewGuid():N}.tmp"),
            Path.Combine(retentionPaths.CustomLoopReceiptRetentionPath, "unexpected-shared-artifact.json")
        };
        foreach (var path in retentionFiles)
        {
            await File.WriteAllTextAsync(path, "unreadable");
        }

        var retentionStore = new CustomLoopControlOperationStore(retentionPaths);
        var retentionPosture = await retentionStore.InspectAsync();
        var retentionCleanup = await retentionStore.CleanupAsync(CleanupCommand("cleanup-overpopulated-shared-inventory"));

        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, cleanupPosture.CleanupBlockReason);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, cleanup.Status);
        Assert.Contains("bounded inventory ceiling", cleanup.Detail, StringComparison.Ordinal);
        Assert.Equal(cleanupFiles.Length, cleanupFiles.Count(File.Exists));
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, retentionPosture.CleanupBlockReason);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, retentionCleanup.Status);
        Assert.Contains("bounded inventory ceiling", retentionCleanup.Detail, StringComparison.Ordinal);
        Assert.Equal(retentionFiles.Length, retentionFiles.Count(File.Exists));
    }

    [Fact]
    public async Task Active_cleanup_journal_inspection_reports_empty_and_audit_blocked_retention_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp);
        var store = new CustomLoopControlOperationStore(paths, timeProvider: time);

        var empty = await store.InspectActiveCleanupJournalAsync();
        var created = await store.BeginAsync(Pending("control-inspect-active-cleanup", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var completed = Complete(created.Operation!, _timestamp);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        lease.Dispose();

        time.UtcNow = completed.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        var cleanup = await store.CleanupAsync(CleanupCommand("cleanup-inspect-active"));
        var active = await store.InspectActiveCleanupJournalAsync();

        Assert.Equal(0, empty.Utf8Bytes);
        Assert.Null(empty.Stage);
        Assert.Equal(CustomLoopReceiptCleanupStatus.AuditUnavailable, cleanup.Status);
        Assert.True(active.Utf8Bytes > 0);
        Assert.Equal(CustomLoopReceiptCleanupStage.IntentPersisted, active.Stage);
        Assert.Equal(CustomLoopReceiptCleanupOutcome.Unknown, active.Outcome);
        Assert.Equal(cleanup.Journal!.OwnershipAcquiredAtUtc + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow, active.RecoveryAvailableAtUtc);
    }

    [Fact]
    public async Task Cleanup_reports_corrupt_when_terminal_journal_cannot_be_archived_before_a_new_request()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopControlOperationStore(paths, timeProvider: new MutableTimeProvider(_timestamp));

        var first = await store.CleanupAsync(CleanupCommand("cleanup-terminal-archive"));
        Directory.CreateDirectory(Path.GetDirectoryName(paths.CustomLoopLifecycleControlReceiptCleanupHistoryPath)!);
        await File.WriteAllTextAsync(paths.CustomLoopLifecycleControlReceiptCleanupHistoryPath, "history-path-is-not-a-directory");
        var blocked = await store.CleanupAsync(CleanupCommand("cleanup-after-unarchivable-terminal"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, first.Status);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, blocked.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, blocked.BlockReason);
        Assert.Equal(first.Journal!.Request.OperationId, blocked.Journal!.Request.OperationId);
    }

    [Fact]
    public async Task Inspection_and_cleanup_distinguish_pending_unaudited_and_corrupt_control_receipt_evidence()
    {
        using var pendingWorkspace = new TestWorkspace();
        var pendingPaths = new WorkspacePaths(pendingWorkspace.RootPath);
        var pendingStore = new CustomLoopControlOperationStore(pendingPaths, timeProvider: new MutableTimeProvider(_timestamp));
        var pending = await pendingStore.BeginAsync(Pending("control-pending-inspection", AuditSchema.Actors.Web));
        using var pendingLease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(pending.Lease);
        var pendingCleanup = await pendingStore.CleanupAsync(CleanupCommand("cleanup-pending-inspection"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, pendingCleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.PendingEvidence, pendingCleanup.BlockReason);

        using var unauditedWorkspace = new TestWorkspace();
        var unauditedPaths = new WorkspacePaths(unauditedWorkspace.RootPath);
        var unauditedStore = new CustomLoopControlOperationStore(unauditedPaths, timeProvider: new MutableTimeProvider(_timestamp));
        var created = await unauditedStore.BeginAsync(Pending("control-unaudited-inspection", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var unaudited = Complete(created.Operation!, _timestamp) with { OutcomeAuditRecorded = false };
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await unauditedStore.CompleteAsync(unaudited)).Status);
        lease.Dispose();
        var unauditedCleanup = await unauditedStore.CleanupAsync(CleanupCommand("cleanup-unaudited-inspection"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, unauditedCleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.UnauditedEvidence, unauditedCleanup.BlockReason);

        using var corruptWorkspace = new TestWorkspace();
        var corruptPaths = new WorkspacePaths(corruptWorkspace.RootPath);
        Directory.CreateDirectory(corruptPaths.CustomLoopControlOperationsPath);
        await using (var oversized = new FileStream(Path.Combine(corruptPaths.CustomLoopControlOperationsPath, "control-oversized.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            oversized.SetLength((64 * 1024) + 1);
        }

        var corruptCleanup = await new CustomLoopControlOperationStore(corruptPaths).CleanupAsync(CleanupCommand("cleanup-oversized-evidence"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, corruptCleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, corruptCleanup.BlockReason);
    }

    [Fact]
    public async Task Cleanup_fails_closed_for_invalid_filename_and_missing_ownership_in_inventory_reads()
    {
        using var filenameWorkspace = new TestWorkspace();
        var filenamePaths = new WorkspacePaths(filenameWorkspace.RootPath);
        Directory.CreateDirectory(filenamePaths.CustomLoopControlOperationsPath);
        await File.WriteAllTextAsync(Path.Combine(filenamePaths.CustomLoopControlOperationsPath, "not a canonical id.json"), "{}");
        var filenameFailure = await new CustomLoopControlOperationStore(filenamePaths).CleanupAsync(CleanupCommand("cleanup-invalid-filename"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, filenameFailure.Status);

        using var ownershipWorkspace = new TestWorkspace();
        var ownershipPaths = new WorkspacePaths(ownershipWorkspace.RootPath);
        var store = new CustomLoopControlOperationStore(ownershipPaths, timeProvider: new MutableTimeProvider(_timestamp));
        var created = await store.BeginAsync(Pending("control-inventory-owner", AuditSchema.Actors.Web));
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        var receiptPath = Path.Combine(ownershipPaths.CustomLoopControlOperationsPath, "control-inventory-owner.json");
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!.AsObject();
        receipt.Remove("ownerGenerationId");
        receipt.Remove("ownerProcessId");
        receipt.Remove("ownerAcquiredAtUtc");
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        lease.Dispose();
        var ownershipFailure = await store.CleanupAsync(CleanupCommand("cleanup-missing-owner"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, ownershipFailure.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, ownershipFailure.BlockReason);
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
        return CustomLoopReceiptCleanupRequestFactory.Create(CleanupCommand(operationId), requestedAtUtc);
    }

    private static CustomLoopReceiptCleanupCommand CleanupCommand(string operationId)
    {
        return new CustomLoopReceiptCleanupCommand(
            CustomLoopReceiptCleanupCommand.CurrentSchemaVersion,
            CustomLoopReceiptArtifactClass.LifecycleControlReceipt,
            operationId,
            AuditSchema.Actors.Web,
            "web",
            4,
            64 * 1024);
    }

    private static CustomLoopReceiptCleanupJournal ReadCleanupJournal(string path)
    {
        return CustomLoopReceiptRetentionContractCodec.DeserializeCleanupJournal(File.ReadAllBytes(path));
    }

    private static async Task<CustomLoopReceiptCleanupCandidate> CreateCandidateAsync(WorkspacePaths paths, CustomLoopControlOperation completed, DateTimeOffset expiry)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(paths.CustomLoopControlOperationsPath, completed.OperationId + ".json"));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var proof = new CustomLoopExpiredOperationProof(CustomLoopExpiredOperationProof.CurrentSchemaVersion, CustomLoopReceiptArtifactClass.LifecycleControlReceipt, null, null, null, completed.OperationId, completed.RequestHash, hash, completed.UpdatedAtUtc, expiry);
        return new CustomLoopReceiptCleanupCandidate(completed.OperationId, hash, bytes.Length, CustomLoopReceiptArtifactCategory.Compactable, true, true, proof, null);
    }

    private static async Task WriteCleanupJournalAsync(WorkspacePaths paths, CustomLoopReceiptCleanupJournal journal)
    {
        Directory.CreateDirectory(paths.CustomLoopControlReceiptCleanupPath);
        await File.WriteAllBytesAsync(Path.Combine(paths.CustomLoopControlReceiptCleanupPath, "active.json"), CustomLoopReceiptRetentionContractCodec.SerializeCleanupJournal(journal));
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

    private sealed class ThrowOnSecondAppendAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            if (Events.Count == 2)
            {
                throw new IOException("Injected outcome-audit failure after durable event append.");
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
    }

    private sealed class ThrowingReadAuditLog : IAuditLog
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => throw new IOException("Injected audit-read failure.");
    }

    private static string NestedJson(int depth) => string.Concat(Enumerable.Repeat("{\"nested\":", depth)) + "null" + new string('}', depth);

    private static Process StartControlOperationHost(string workspaceRoot, CustomLoopControlOperation pending)
        => CancellationHostProcess.Start(
            "hold-control",
            workspaceRoot,
            pending.Kind.ToString(),
            pending.RunId,
            pending.ExpectedLifecycleVersion.ToString(),
            pending.OperationId);
}
