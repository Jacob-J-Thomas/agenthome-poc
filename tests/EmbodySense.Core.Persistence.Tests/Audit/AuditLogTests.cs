using System.Diagnostics;
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
    public async Task Sequential_recorder_reconciles_recorded_evidence_after_external_response_loss()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var process = StartRecordThenExitProcess(workspace.RootPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = await process.StandardError.ReadToEndAsync(timeout.Token);
        Assert.True(process.ExitCode != 0, $"Child unexpectedly completed normally.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        var evidenceHash = Hash('a');

        var replay = await new AuditLog(paths).RecordOnceAsync(
            GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash),
            evidenceHash,
            Event());

        Assert.Equal(GovernedLoopSequentialAuditRecordStatus.AlreadyRecorded, replay.Status);
        Assert.Single(await File.ReadAllLinesAsync(paths.EventsLogPath));
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

    private static string Hash(char value) => new(value, 64);
}
