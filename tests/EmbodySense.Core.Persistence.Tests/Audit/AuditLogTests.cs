using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Tests.Verification;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Audit;

public sealed class AuditLogTests
{
    private const int RecordThenExitCode = 91;
    private static readonly DateTimeOffset _timestamp = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadTailAsync_returns_last_events_in_order()
    {
        using var workspace = new TestWorkspace();
        var auditLog = new AuditLog(new WorkspacePaths(workspace.RootPath));

        await auditLog.AppendAsync(AuditEvent.Create("test", "first", "target", "ok", "first event"));
        await auditLog.AppendAsync(AuditEvent.Create("test", "second", "target", "ok", "second event"));
        await auditLog.AppendAsync(AuditEvent.Create("test", "third", "target", "ok", "third event"));

        var events = await auditLog.ReadTailAsync(2);

        Assert.Collection(
            events,
            auditEvent => Assert.Equal("second", auditEvent.Action),
            auditEvent => Assert.Equal("third", auditEvent.Action));
    }

    [Fact]
    public async Task ReadTailAsync_returns_empty_when_log_file_is_missing()
    {
        using var workspace = new TestWorkspace();
        var auditLog = new AuditLog(new WorkspacePaths(workspace.RootPath));

        var events = await auditLog.ReadTailAsync(10);

        Assert.Empty(events);
    }

    [Fact]
    public async Task ReadTailAsync_rejects_non_positive_limits()
    {
        using var workspace = new TestWorkspace();
        var auditLog = new AuditLog(new WorkspacePaths(workspace.RootPath));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => auditLog.ReadTailAsync(0));
    }

    [Fact]
    public async Task ReadTailAsync_skips_malformed_lines()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AuditPath);
        await File.WriteAllTextAsync(paths.EventsLogPath, "{not-json}" + Environment.NewLine);
        var auditLog = new AuditLog(paths);

        await auditLog.AppendAsync(AuditEvent.Create("test", "valid", "target", "ok", "valid event"));

        var auditEvent = Assert.Single(await auditLog.ReadTailAsync(10));
        Assert.Equal("valid", auditEvent.Action);
    }

    [Fact]
    public async Task ReadTailAsync_remains_tolerant_of_unknown_event_properties()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AuditPath);
        var json = JsonSerializer.Serialize(Event())[..^1] + ",\"futureProperty\":true}\n";
        await File.WriteAllTextAsync(paths.EventsLogPath, json);

        var auditEvent = Assert.Single(await new AuditLog(paths).ReadTailAsync(1));

        Assert.Equal("node-outcome", auditEvent.Action);
    }

    [Fact]
    public async Task Sequential_recorder_replays_exact_event_after_restart_and_canonicalizes_metadata()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var evidenceHash = Hash('a');
        var operationId = GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash);
        var firstEvent = Event(new Dictionary<string, object?>
        {
            ["zeta"] = 7,
            ["sequentialEvidenceHash"] = evidenceHash,
            ["auditOperationId"] = "ordinary-domain-operation",
            ["active"] = true,
            ["optional"] = null,
            ["negativeZero"] = -0.0d,
            ["scientific"] = 1.25e100,
        });
        var replayEvent = Event(new Dictionary<string, object?>
        {
            ["optional"] = null,
            ["active"] = true,
            ["auditOperationId"] = "ordinary-domain-operation",
            ["sequentialEvidenceHash"] = evidenceHash,
            ["zeta"] = 7L,
            ["negativeZero"] = -0.0d,
            ["scientific"] = 1.25e100,
        });

        var recorded = await new AuditLog(paths).RecordOnceAsync(operationId, evidenceHash, firstEvent);
        var replay = await new AuditLog(paths).RecordOnceAsync(operationId, evidenceHash, replayEvent);

        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.Recorded, recorded.Status);
        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.AlreadyRecorded, replay.Status);
        Assert.Single(await File.ReadAllLinesAsync(paths.EventsLogPath));
        var durable = Assert.Single(await new AuditLog(paths).ReadTailAsync(10));
        Assert.Equal(operationId, MetadataString(durable, "governedLoopSequentialAuditOperationId"));
        Assert.Equal(evidenceHash, MetadataString(durable, "governedLoopSequentialAuditEvidenceHash"));
    }

    [Fact]
    public async Task Sequential_recorder_allows_legacy_metadata_that_matches_other_subsystem_keys()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var legacy = Event(new Dictionary<string, object?>
        {
            ["auditOperationId"] = "credential-outbox-operation",
            ["sequentialEvidenceHash"] = Hash('b'),
            ["nestedLegacyEvidence"] = new Dictionary<string, object?> { ["count"] = 1 },
        });
        await new AuditLog(paths).AppendAsync(legacy);
        var evidenceHash = Hash('a');

        var result = await new AuditLog(paths).RecordOnceAsync(
            GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash),
            evidenceHash,
            Event());

        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.Recorded, result.Status);
        Assert.Equal(2, (await new AuditLog(paths).ReadTailAsync(10)).Count);
    }

    [Fact]
    public async Task Sequential_recorder_conflicts_on_divergent_evidence_or_event_without_appending()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var evidenceHash = Hash('a');
        var operationId = GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash);
        Assert.Equal(
            GovernedLoopSequentialAuditRecordStatus.Recorded,
            (await new AuditLog(paths).RecordOnceAsync(operationId, evidenceHash, Event())).Status);
        var original = await File.ReadAllBytesAsync(paths.EventsLogPath);

        var changedEvidence = await new AuditLog(paths).RecordOnceAsync(operationId, Hash('b'), Event());
        var changedEvent = await new AuditLog(paths).RecordOnceAsync(operationId, evidenceHash, Event() with { Detail = "different" });

        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.Conflict, changedEvidence.Status);
        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.Conflict, changedEvent.Status);
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.EventsLogPath));
    }

    [Fact]
    public async Task Sequential_recorder_serializes_concurrent_instances_to_one_logical_record()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var evidenceHash = Hash('a');
        var operationId = GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash);
        var tasks = Enumerable.Range(0, 16)
            .Select(_ => new AuditLog(paths).RecordOnceAsync(operationId, evidenceHash, Event()))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => result.Status == GovernedLoopSequentialAuditRecordStatus.Recorded);
        Assert.Equal(15, results.Count(result => result.Status == GovernedLoopSequentialAuditRecordStatus.AlreadyRecorded));
        Assert.Single(await File.ReadAllLinesAsync(paths.EventsLogPath));
    }

    [Fact]
    public async Task Two_os_processes_append_two_complete_records_through_one_canonical_boundary()
    {
        using var workspace = new TestWorkspace();
        var releasePath = workspace.File("audit-append.release");
        var firstReadyPath = workspace.File("audit-append-first.ready");
        var secondReadyPath = workspace.File("audit-append-second.ready");
        var firstResultPath = workspace.File("audit-append-first.result");
        var secondResultPath = workspace.File("audit-append-second.result");
        using var first = CancellationHostProcess.StartOwned("audit-append", workspace.RootPath, "first", firstReadyPath, releasePath, firstResultPath);
        using var second = CancellationHostProcess.StartOwned("audit-append", workspace.RootPath, "second", secondReadyPath, releasePath, secondResultPath);
        var children = Children(first, firstReadyPath, firstResultPath, second, secondReadyPath, secondResultPath);

        await CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync("audit append", children, TimeSpan.FromSeconds(30));
        await File.WriteAllTextAsync(releasePath, "release");
        await CrossProcessReadinessDiagnostics.WaitForChildrenCompletedAsync("audit append", "append", children, TimeSpan.FromSeconds(30));

        Assert.Equal("appended", await File.ReadAllTextAsync(firstResultPath));
        Assert.Equal("appended", await File.ReadAllTextAsync(secondResultPath));
        var events = await new AuditLog(new WorkspacePaths(workspace.RootPath)).ReadTailAsync(10);
        Assert.Equal(["append-first", "append-second"], events.Select(auditEvent => auditEvent.Action).Order(StringComparer.Ordinal));
        Assert.All(await File.ReadAllLinesAsync(new WorkspacePaths(workspace.RootPath).EventsLogPath), line => Assert.NotNull(JsonSerializer.Deserialize<AuditEvent>(line)));
    }

    [Fact]
    public async Task Two_os_processes_record_the_same_exact_sequential_audit_once()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var releasePath = workspace.File("sequential-audit.release");
        var firstReadyPath = workspace.File("sequential-audit-first.ready");
        var secondReadyPath = workspace.File("sequential-audit-second.ready");
        var firstResultPath = workspace.File("sequential-audit-first.result");
        var secondResultPath = workspace.File("sequential-audit-second.result");
        using var first = CancellationHostProcess.StartOwned("sequential-audit-record", workspace.RootPath, firstReadyPath, releasePath, firstResultPath);
        using var second = CancellationHostProcess.StartOwned("sequential-audit-record", workspace.RootPath, secondReadyPath, releasePath, secondResultPath);
        var children = Children(first, firstReadyPath, firstResultPath, second, secondReadyPath, secondResultPath);

        await CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync("sequential audit", children, TimeSpan.FromSeconds(30));
        await File.WriteAllTextAsync(releasePath, "release");
        await CrossProcessReadinessDiagnostics.WaitForChildrenCompletedAsync("sequential audit", "record", children, TimeSpan.FromSeconds(30));

        var dispositions = new[] { await File.ReadAllTextAsync(firstResultPath), await File.ReadAllTextAsync(secondResultPath) };
        var statuses = dispositions.Select(disposition => disposition.Split('|', 2)[0]).ToArray();
        Assert.True(statuses.Order(StringComparer.Ordinal).SequenceEqual(["AlreadyRecorded", "Recorded"]), string.Join(Environment.NewLine, dispositions));
        Assert.Single(await File.ReadAllLinesAsync(paths.EventsLogPath));
        var durable = Assert.Single(await new AuditLog(paths).ReadTailAsync(10));
        var evidenceHash = Hash('a');
        Assert.Equal(GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash), MetadataString(durable, "governedLoopSequentialAuditOperationId"));
        Assert.Equal(evidenceHash, MetadataString(durable, "governedLoopSequentialAuditEvidenceHash"));
        Assert.Equal(1, MetadataInt32(durable, "governedLoopSequentialAuditSchemaVersion"));
        Assert.Equal("loop-runtime", durable.Actor);
        Assert.Equal("node-outcome", durable.Action);
        Assert.Equal("run-1", durable.Target);
        Assert.Equal("succeeded", durable.Outcome);
    }

    [Fact]
    public async Task Append_waits_for_cross_process_contention_and_completes_after_release()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var auditLog = new AuditLog(paths);
        await auditLog.AppendAsync(Event());
        var releasePath = workspace.File("audit-lock.release");
        var readyPath = workspace.File("audit-lock.ready");
        var unusedResultPath = workspace.File("audit-lock.unused");
        using var holder = CancellationHostProcess.StartOwned("audit-hold-lock", AuditLockPath(paths), readyPath, releasePath);
        var child = new CrossProcessReadinessChild("holder", holder, readyPath, unusedResultPath);
        await CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync("audit lock holder", [child], TimeSpan.FromSeconds(30));

        try
        {
            var append = auditLog.AppendAsync(Event() with { Action = "after-release" });
            await File.WriteAllTextAsync(releasePath, "release");
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, holder.ExitCode);
            await append.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await ReleaseHolderAsync(holder, releasePath);
        }

        Assert.Equal(["node-outcome", "after-release"], (await auditLog.ReadTailAsync(10)).Select(auditEvent => auditEvent.Action));
    }

    [Fact]
    public async Task Persistent_cross_process_contention_has_one_finite_process_and_sidecar_deadline()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var auditLog = new AuditLog(paths);
        await auditLog.AppendAsync(Event());
        using var externalLock = CrossProcessExclusiveFileLock.Acquire(AuditLockPath(paths));

        var started = Stopwatch.StartNew();
        var pendingAppend = auditLog.AppendAsync(Event() with { Action = "contended" });
        var appendTimeout = Assert.ThrowsAsync<TimeoutException>(() => pendingAppend);
        var evidenceHash = Hash('a');
        var pendingSequential = auditLog.RecordOnceAsync(GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash), evidenceHash, Event());
        await Task.WhenAll(appendTimeout, pendingSequential).WaitAsync(TimeSpan.FromSeconds(12));

        var exception = await appendTimeout;
        Assert.Contains("complete bounded wait", exception.Message, StringComparison.Ordinal);
        var sequential = await pendingSequential;
        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.Unavailable, sequential.Status);
        Assert.InRange(started.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10));
        Assert.Single(await File.ReadAllLinesAsync(paths.EventsLogPath));
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_cross_process_ownership_propagates_without_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var auditLog = new AuditLog(paths);
        await auditLog.AppendAsync(Event());
        var original = await File.ReadAllBytesAsync(paths.EventsLogPath);
        using var externalLock = CrossProcessExclusiveFileLock.Acquire(AuditLockPath(paths));
        using var cancellation = new CancellationTokenSource();

        var append = auditLog.AppendAsync(Event() with { Action = "cancelled" }, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => append);
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.EventsLogPath));
    }

    [Fact]
    public async Task Killed_cross_process_owner_releases_the_boundary_for_recovery()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var auditLog = new AuditLog(paths);
        await auditLog.AppendAsync(Event());
        var releasePath = workspace.File("owner-death.release");
        var readyPath = workspace.File("owner-death.ready");
        using var holder = CancellationHostProcess.StartOwned("audit-hold-lock", AuditLockPath(paths), readyPath, releasePath);
        var child = new CrossProcessReadinessChild("owner", holder, readyPath, workspace.File("owner-death.unused"));
        await CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync("audit owner death", [child], TimeSpan.FromSeconds(30));

        holder.Ownership.TerminateProcessTree();
        await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await auditLog.AppendAsync(Event() with { Action = "recovered" }).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("recovered", (await auditLog.ReadTailAsync(10))[^1].Action);
    }

    [Fact]
    public async Task Crash_produced_incomplete_tail_remains_unavailable_and_preserves_the_valid_prefix_exactly()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var auditLog = new AuditLog(paths);
        await auditLog.AppendAsync(Event() with { Action = "valid-prefix" });
        var readyPath = workspace.File("incomplete-tail.ready");
        var releasePath = workspace.File("incomplete-tail.release");
        using var holder = CancellationHostProcess.StartOwned("audit-append-incomplete-tail-and-hold", workspace.RootPath, readyPath, releasePath);
        var child = new CrossProcessReadinessChild("incomplete-tail-owner", holder, readyPath, workspace.File("incomplete-tail.unused"));
        byte[] crashedBytes;

        try
        {
            await CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync("incomplete audit tail", [child], TimeSpan.FromSeconds(30));
            crashedBytes = await File.ReadAllBytesAsync(paths.EventsLogPath);
            holder.Ownership.TerminateProcessTree();
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotEqual(0, holder.ExitCode);
        }
        finally
        {
            await TerminateHolderAsync(holder, releasePath);
        }

        var evidenceHash = Hash('a');
        var result = await auditLog.RecordOnceAsync(GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash), evidenceHash, Event());

        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.Unavailable, result.Status);
        Assert.Equal(crashedBytes, await File.ReadAllBytesAsync(paths.EventsLogPath));
        var validPrefix = Assert.Single(await auditLog.ReadTailAsync(10));
        Assert.Equal("valid-prefix", validPrefix.Action);
    }

    [Fact]
    public async Task ReadTailAsync_remains_available_while_a_cross_process_writer_owns_the_boundary()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var auditLog = new AuditLog(paths);
        await auditLog.AppendAsync(Event());
        var releasePath = workspace.File("reader.release");
        var readyPath = workspace.File("reader.ready");
        using var holder = CancellationHostProcess.StartOwned("audit-hold-lock", AuditLockPath(paths), readyPath, releasePath);
        var child = new CrossProcessReadinessChild("reader-holder", holder, readyPath, workspace.File("reader.unused"));
        await CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync("audit reader", [child], TimeSpan.FromSeconds(30));

        try
        {
            var durable = Assert.Single(await auditLog.ReadTailAsync(10).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("node-outcome", durable.Action);
        }
        finally
        {
            await ReleaseHolderAsync(holder, releasePath);
        }

        Assert.Equal(0, holder.ExitCode);
    }

    [Fact]
    public async Task Concurrent_append_record_once_and_read_tail_calls_complete_without_lock_inversion()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var auditLog = new AuditLog(paths);
        var evidenceHash = Hash('a');
        var operationId = GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appends = Enumerable.Range(0, 16)
            .Select(index => Task.Run(async () =>
            {
                await start.Task;
                await auditLog.AppendAsync(Event() with { Action = $"append-{index:D2}" });
            }))
            .ToArray();
        var records = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return await auditLog.RecordOnceAsync(operationId, evidenceHash, Event());
            }))
            .ToArray();
        var reads = Task.Run(async () =>
        {
            await start.Task;
            for (var attempt = 0; attempt < 64; attempt++)
            {
                _ = await auditLog.ReadTailAsync(10);
            }
        });

        start.SetResult();
        await Task.WhenAll([.. appends, .. records, reads]).WaitAsync(TimeSpan.FromSeconds(30));
        var dispositions = await Task.WhenAll(records);

        Assert.Single(dispositions, result => result.Status == GovernedLoopSequentialAuditRecordStatus.Recorded);
        Assert.Equal(15, dispositions.Count(result => result.Status == GovernedLoopSequentialAuditRecordStatus.AlreadyRecorded));
        Assert.Equal(17, await CountNonBlankLinesAsync(paths.EventsLogPath));
    }

    [Fact]
    public async Task Mutation_boundary_refuses_a_reparse_sidecar_without_touching_the_ledger()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var auditLog = new AuditLog(paths);
        await auditLog.AppendAsync(Event());
        var original = await File.ReadAllBytesAsync(paths.EventsLogPath);
        File.Delete(AuditLockPath(paths));
        var outside = workspace.File("outside.lock");
        await File.WriteAllTextAsync(outside, string.Empty);
        File.CreateSymbolicLink(AuditLockPath(paths), outside);
        var evidenceHash = Hash('a');

        var sequential = await auditLog.RecordOnceAsync(GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash), evidenceHash, Event());

        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.Unavailable, sequential.Status);
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.EventsLogPath));
        Assert.Equal(0, new FileInfo(outside).Length);
    }

    [Fact]
    public async Task Mutation_boundary_refuses_a_hard_linked_ledger_and_releases_the_rejected_handle()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var auditLog = new AuditLog(paths);
        await auditLog.AppendAsync(Event());
        var original = await File.ReadAllBytesAsync(paths.EventsLogPath);
        var aliasPath = workspace.File("events-alias.ndjson");
        Assert.True(TryCreateHardLink(aliasPath, paths.EventsLogPath));
        var evidenceHash = Hash('a');

        var sequential = await auditLog.RecordOnceAsync(GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash), evidenceHash, Event());

        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.Unavailable, sequential.Status);
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.EventsLogPath));
        File.Delete(aliasPath);
        File.Delete(paths.EventsLogPath);
        Assert.False(File.Exists(paths.EventsLogPath));
    }

    [Fact]
    public async Task Append_repairs_a_complete_final_record_but_refuses_an_incomplete_unknown_tail()
    {
        using var completeWorkspace = new TestWorkspace();
        var completePaths = new WorkspacePaths(completeWorkspace.RootPath);
        var completeLog = new AuditLog(completePaths);
        await completeLog.AppendAsync(Event());
        await using (var stream = new FileStream(completePaths.EventsLogPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(stream.Length - Environment.NewLine.Length);
        }

        await completeLog.AppendAsync(Event() with { Action = "after-complete-tail" });

        Assert.Equal(["node-outcome", "after-complete-tail"], (await completeLog.ReadTailAsync(10)).Select(auditEvent => auditEvent.Action));

        using var incompleteWorkspace = new TestWorkspace();
        var incompletePaths = new WorkspacePaths(incompleteWorkspace.RootPath);
        Directory.CreateDirectory(incompletePaths.AuditPath);
        await File.WriteAllTextAsync(incompletePaths.EventsLogPath, "{\"timestampUtc\":");
        var incomplete = await File.ReadAllBytesAsync(incompletePaths.EventsLogPath);

        await Assert.ThrowsAsync<IOException>(() => new AuditLog(incompletePaths).AppendAsync(Event()));
        Assert.Equal(incomplete, await File.ReadAllBytesAsync(incompletePaths.EventsLogPath));
    }

    [Fact]
    public async Task Sequential_recorder_propagates_cancellation_without_creating_a_record()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var evidenceHash = Hash('a');
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AuditLog(paths).RecordOnceAsync(
            GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash),
            evidenceHash,
            Event(),
            cancellation.Token));

        Assert.False(File.Exists(paths.EventsLogPath));
    }

    [Theory]
    [InlineData("{not-json}")]
    [InlineData("{not-json}\n")]
    public async Task Sequential_recorder_leaves_malformed_authoritative_evidence_unchanged(string content)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AuditPath);
        var original = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(paths.EventsLogPath, original);
        var evidenceHash = Hash('a');

        var result = await new AuditLog(paths).RecordOnceAsync(
            GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash),
            evidenceHash,
            Event());

        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.Unavailable, result.Status);
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.EventsLogPath));
    }

    [Fact]
    public async Task Sequential_recorder_restores_only_a_missing_newline_after_a_complete_record()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var evidenceHash = Hash('a');
        var operationId = GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash);
        Assert.Equal(
            GovernedLoopSequentialAuditRecordStatus.Recorded,
            (await new AuditLog(paths).RecordOnceAsync(operationId, evidenceHash, Event())).Status);
        await using (var stream = new FileStream(paths.EventsLogPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(stream.Length - 1);
        }

        var result = await new AuditLog(paths).RecordOnceAsync(operationId, evidenceHash, Event());
        var bytes = await File.ReadAllBytesAsync(paths.EventsLogPath);

        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.AlreadyRecorded, result.Status);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.Single(await File.ReadAllLinesAsync(paths.EventsLogPath));
    }

    [Fact]
    public async Task Sequential_recorder_rejects_an_oversized_ledger_without_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AuditPath);
        const long OversizedLength = (16L * 1024 * 1024) + 1;
        await using (var stream = new FileStream(paths.EventsLogPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.WriteByte(17);
            stream.SetLength(OversizedLength);
            stream.Position = OversizedLength - 1;
            stream.WriteByte(23);
        }
        var evidenceHash = Hash('a');

        var result = await new AuditLog(paths).RecordOnceAsync(
            GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash),
            evidenceHash,
            Event());

        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.Unavailable, result.Status);
        var info = new FileInfo(paths.EventsLogPath);
        Assert.Equal(OversizedLength, info.Length);
        await using var verification = new FileStream(paths.EventsLogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(17, verification.ReadByte());
        verification.Position = OversizedLength - 1;
        Assert.Equal(23, verification.ReadByte());
    }

    [Fact]
    public async Task Sequential_recorder_rejects_duplicate_physical_operation_records()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var evidenceHash = Hash('a');
        var operationId = GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash);
        Assert.Equal(
            GovernedLoopSequentialAuditRecordStatus.Recorded,
            (await new AuditLog(paths).RecordOnceAsync(operationId, evidenceHash, Event())).Status);
        var line = await File.ReadAllTextAsync(paths.EventsLogPath);
        await File.AppendAllTextAsync(paths.EventsLogPath, line);
        var duplicated = await File.ReadAllBytesAsync(paths.EventsLogPath);

        var result = await new AuditLog(paths).RecordOnceAsync(operationId, evidenceHash, Event());

        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.Conflict, result.Status);
        Assert.Equal(duplicated, await File.ReadAllBytesAsync(paths.EventsLogPath));
    }

    [Fact]
    public async Task Sequential_recorder_rejects_non_scalar_or_non_finite_candidate_metadata()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var evidenceHash = Hash('a');
        var operationId = GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash);
        var invalidValues = new object?[]
        {
            new Dictionary<string, string> { ["nested"] = "value" },
            new[] { "value" },
            double.NaN,
            double.PositiveInfinity,
            DateTimeOffset.UtcNow,
            new string('x', (16 * 1024) + 1),
        };

        foreach (var invalid in invalidValues)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => new AuditLog(paths).RecordOnceAsync(
                operationId,
                evidenceHash,
                Event(new Dictionary<string, object?> { ["invalid"] = invalid })));
        }

        Assert.False(File.Exists(paths.EventsLogPath));
    }

    [Fact]
    public async Task Sequential_recorder_returns_unavailable_when_the_log_path_is_not_a_file()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.EventsLogPath);
        var evidenceHash = Hash('a');

        var result = await new AuditLog(paths).RecordOnceAsync(
            GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash),
            evidenceHash,
            Event());

        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.Unavailable, result.Status);
        Assert.True(Directory.Exists(paths.EventsLogPath));
    }

    [Fact]
    public async Task Sequential_recorder_reconciles_a_complete_record_when_process_exit_leaves_the_caller_outcome_unknown()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var process = StartRecordThenExitProcess(workspace.RootPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = await process.StandardError.ReadToEndAsync(timeout.Token);
        Assert.True(process.ExitCode == RecordThenExitCode, $"Expected exit {RecordThenExitCode}, got {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        var evidenceHash = Hash('a');
        var operationId = GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash);

        var replay = await new AuditLog(paths).RecordOnceAsync(
            operationId,
            evidenceHash,
            Event());

        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.AlreadyRecorded, replay.Status);
        Assert.Single(await File.ReadAllLinesAsync(paths.EventsLogPath));
        var durable = Assert.Single(await new AuditLog(paths).ReadTailAsync(10));
        Assert.Equal(operationId, MetadataString(durable, "governedLoopSequentialAuditOperationId"));
        Assert.Equal(evidenceHash, MetadataString(durable, "governedLoopSequentialAuditEvidenceHash"));
        Assert.Equal("loop-runtime", durable.Actor);
        Assert.Equal("node-outcome", durable.Action);
        Assert.Equal("run-1", durable.Target);
        Assert.Equal("succeeded", durable.Outcome);
    }

    private static Process StartRecordThenExitProcess(string workspaceRoot)
        => CancellationHostProcess.Start("sequential-audit-record-then-exit", workspaceRoot);

    private static AuditEvent Event(IReadOnlyDictionary<string, object?>? metadata = null)
        => new(
            _timestamp,
            "loop-runtime",
            "node-outcome",
            "run-1",
            "succeeded",
            "Deterministic node outcome recorded.",
            metadata ?? new Dictionary<string, object?>());

    private static string MetadataString(AuditEvent auditEvent, string key)
    {
        var value = auditEvent.Metadata[key];
        return value is JsonElement element ? Assert.IsType<string>(element.GetString()) : Assert.IsType<string>(value);
    }

    private static int MetadataInt32(AuditEvent auditEvent, string key)
    {
        var value = auditEvent.Metadata[key];
        return value is JsonElement element ? element.GetInt32() : Assert.IsType<int>(value);
    }

    private static IReadOnlyList<CrossProcessReadinessChild> Children(
        CrossProcessProcess first,
        string firstReadyPath,
        string firstResultPath,
        CrossProcessProcess second,
        string secondReadyPath,
        string secondResultPath)
        =>
        [
            new CrossProcessReadinessChild("first", first, firstReadyPath, firstResultPath),
            new CrossProcessReadinessChild("second", second, secondReadyPath, secondResultPath),
        ];

    private static string AuditLockPath(WorkspacePaths paths) => Path.Combine(paths.AuditPath, ".events.ndjson.mutation.lock");

    private static async Task<int> CountNonBlankLinesAsync(string path)
        => (await File.ReadAllLinesAsync(path)).Count(line => !string.IsNullOrWhiteSpace(line));

    private static async Task ReleaseHolderAsync(CrossProcessProcess holder, string releasePath)
    {
        await File.WriteAllTextAsync(releasePath, "release");
        try
        {
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            holder.Ownership.TerminateProcessTree();
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task TerminateHolderAsync(CrossProcessProcess holder, string releasePath)
    {
        if (!holder.HasExited)
        {
            holder.Ownership.TerminateProcessTree();
        }

        try
        {
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            await File.WriteAllTextAsync(releasePath, "release");
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static bool TryCreateHardLink(string aliasPath, string targetPath)
        => OperatingSystem.IsWindows()
            ? CreateWindowsHardLink(aliasPath, targetPath, IntPtr.Zero)
            : CreateUnixHardLink(targetPath, aliasPath) == 0;

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateWindowsHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateUnixHardLink(string existingPath, string newPath);

    private static string Hash(char value) => new(value, 64);
}
