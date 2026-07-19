using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopTraceRetentionStoreTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-16T12:00:00+00:00");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    [Fact]
    public async Task Inspection_reports_exact_persisted_hash_and_size_and_deletion_releases_live_quota_after_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var terminal = await CreateTerminalRunAsync(store);
        var path = Path.Combine(paths.CustomLoopRunsPath, terminal.LoopId, terminal.Id + ".json");
        var originalBytes = await File.ReadAllBytesAsync(path);
        var expectedHash = Hash(originalBytes);

        var inspection = await store.InspectTraceAsync(terminal.Id);
        Assert.NotNull(inspection);

        Assert.Equal(CustomLoopTraceArtifactKind.LiveTrace, inspection.Kind);
        Assert.Equal(expectedHash, inspection.PersistedArtifactHash);
        Assert.Equal(originalBytes.LongLength, inspection.PersistedArtifactUtf8Bytes);
        Assert.Equal(expectedHash, inspection.OriginalTraceHash);
        Assert.Equal(originalBytes.LongLength, inspection.OriginalTraceUtf8Bytes);
        var request = Request(terminal.Id, expectedHash);
        var deletion = await store.DeleteTerminalTraceAsync(Mutation(request));
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, deletion.Status);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit, deletion.Integrity);
        Assert.DoesNotContain("Initial prompt", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.True(new FileInfo(path).Length <= CustomLoopLimits.MaxRunTraceTombstoneUtf8Bytes);

        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.Marked, await store.MarkTraceDeletionOutcomeAsync(request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted));
        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.Marked, await store.MarkTraceDeletionOutcomeAsync(request.OperationId, CustomLoopTraceDeletionIntegrity.Complete));
        var restarted = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var deleted = await restarted.InspectTraceAsync(terminal.Id);
        Assert.NotNull(deleted);
        var quota = await restarted.GetTraceQuotaAsync();

        Assert.True(deleted.IsDeleted);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.Complete, deleted.Tombstone!.OutcomeIntegrity);
        Assert.Equal(expectedHash, deleted.OriginalTraceHash);
        Assert.Equal(originalBytes.LongLength, deleted.OriginalTraceUtf8Bytes);
        Assert.Equal(0, quota.RetainedTraceCount);
        Assert.Equal(1, quota.TombstoneCount);
        Assert.Equal(0, quota.ActualTraceUtf8Bytes);
        Assert.Equal(deleted.PersistedArtifactUtf8Bytes, quota.TombstoneUtf8Bytes);
        Assert.Equal(quota.TombstoneUtf8Bytes, quota.AccountedTraceUtf8Bytes);
        Assert.Equal(0, quota.ActiveReservationCount);
        Assert.Null(await restarted.GetAsync(terminal.Id));
        await Assert.ThrowsAsync<FormatException>(() => restarted.GetByAdmissionOperationAsync(terminal.AdmissionOperationId));

        var replay = await restarted.DeleteTerminalTraceAsync(Mutation(request));
        var conflict = await restarted.DeleteTerminalTraceAsync(Mutation(request with { Actor = "actor-other" }));
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.AlreadyDeleted, replay.Status);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.Complete, replay.Integrity);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.OperationConflict, conflict.Status);
        Assert.Equal(CustomLoopRunStoreStatus.DeletedIdentityConflict, (await restarted.CreateAsync(CreateAdmittedRun())).Status);
    }

    [Fact]
    public async Task Deletion_rejects_nonterminal_and_hash_mismatch_without_replacing_trace_content()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var admitted = CreateAdmittedRun();
        await store.CreateAsync(admitted);
        var inspection = await store.InspectTraceAsync(admitted.Id);
        Assert.NotNull(inspection);

        var nonterminal = await store.DeleteTerminalTraceAsync(Mutation(Request(admitted.Id, inspection.PersistedArtifactHash)));
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Nonterminal, nonterminal.Status);
        Assert.NotNull(await store.GetAsync(admitted.Id));

        var running = Advance(admitted, CustomLoopRunStatus.Running);
        await store.UpdateAsync(running, admitted.LifecycleVersion);
        var terminal = Advance(running, CustomLoopRunStatus.Completed);
        await store.UpdateAsync(terminal, running.LifecycleVersion);
        var mismatchRequest = Request(terminal.Id, new string('a', CustomLoopLimits.Sha256HexCharacters), "delete-other");
        var mismatch = await store.DeleteTerminalTraceAsync(Mutation(mismatchRequest));

        Assert.Equal(CustomLoopTraceDeletionStoreStatus.HashMismatch, mismatch.Status);
        Assert.NotNull(await store.GetAsync(terminal.Id));
        Assert.Equal(CustomLoopTraceDeletionOperationState.OutcomeCommitted, (await store.GetTraceDeletionOperationAsync(mismatchRequest.OperationId)).Operation!.State);
    }

    [Fact]
    public async Task Pending_ledger_and_tombstone_first_crash_windows_recover_without_a_second_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var terminal = await CreateTerminalRunAsync(store);
        var inspection = await store.InspectTraceAsync(terminal.Id);
        Assert.NotNull(inspection);
        var request = Request(terminal.Id, inspection.PersistedArtifactHash);
        var mutation = Mutation(request);
        var pending = PendingOperation(mutation);
        await WriteOperationAsync(paths, pending);

        var recoveredBeforeMutation = await new CustomLoopRunStore(paths).DeleteTerminalTraceAsync(mutation);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, recoveredBeforeMutation.Status);
        var firstTombstone = (await store.InspectTraceAsync(terminal.Id))!.Tombstone;
        Assert.NotNull(firstTombstone);

        await WriteOperationAsync(paths, pending);
        var recoveredAfterMutation = await new CustomLoopRunStore(paths).DeleteTerminalTraceAsync(mutation);

        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, recoveredAfterMutation.Status);
        Assert.Equal(firstTombstone, recoveredAfterMutation.Tombstone);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(paths.CustomLoopRunsPath, terminal.LoopId), "*.json"));
        Assert.Equal(CustomLoopTraceDeletionOperationState.OutcomeCommitted, (await store.GetTraceDeletionOperationAsync(request.OperationId)).Operation!.State);
    }

    [Fact]
    public async Task Strict_tombstone_and_operation_readers_fail_closed_on_tamper_corruption_and_unsafe_layout()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var terminal = await CreateTerminalRunAsync(store);
        var inspection = await store.InspectTraceAsync(terminal.Id);
        Assert.NotNull(inspection);
        var request = Request(terminal.Id, inspection.PersistedArtifactHash);
        await store.DeleteTerminalTraceAsync(Mutation(request));
        var tracePath = Path.Combine(paths.CustomLoopRunsPath, terminal.LoopId, terminal.Id + ".json");
        var tombstone = JsonDocument.Parse(await File.ReadAllTextAsync(tracePath)).RootElement;
        var tampered = JsonSerializer.Deserialize<Dictionary<string, object?>>(tombstone.GetRawText(), JsonOptions)!;
        tampered["unknownField"] = true;
        await File.WriteAllTextAsync(tracePath, JsonSerializer.Serialize(tampered, JsonOptions));

        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).InspectTraceAsync(terminal.Id));

        await File.WriteAllTextAsync(tracePath, "{invalid");
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetTraceQuotaAsync());

        Directory.Delete(paths.CustomLoopTraceDeletionOperationsPath, recursive: true);
        Directory.CreateDirectory(paths.CustomLoopTraceDeletionOperationsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopTraceDeletionOperationsPath, "unsafe name.json"), "{}");
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetTraceDeletionOperationAsync(request.OperationId));
    }

    [Fact]
    public async Task Conflict_operation_rejects_a_malformed_embedded_tombstone_on_read_and_replay()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var terminal = await CreateTerminalRunAsync(store);
        var inspection = await store.InspectTraceAsync(terminal.Id);
        Assert.NotNull(inspection);

        var deletionRequest = Request(terminal.Id, inspection.PersistedArtifactHash);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(Mutation(deletionRequest))).Status);
        var conflictRequest = Request(terminal.Id, inspection.PersistedArtifactHash, "delete-conflict");
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.OperationConflict, (await store.DeleteTerminalTraceAsync(Mutation(conflictRequest))).Status);

        var operation = (await store.GetTraceDeletionOperationAsync(conflictRequest.OperationId)).Operation;
        Assert.NotNull(operation?.Tombstone);
        await WriteOperationAsync(paths, operation! with { Tombstone = operation.Tombstone! with { SchemaVersion = 99 } });

        var restarted = new CustomLoopRunStore(paths);
        await Assert.ThrowsAsync<FormatException>(() => restarted.GetTraceDeletionOperationAsync(conflictRequest.OperationId));
        await Assert.ThrowsAsync<FormatException>(() => restarted.DeleteTerminalTraceAsync(Mutation(conflictRequest)));
    }

    private static async Task<CustomLoopRunRecord> CreateTerminalRunAsync(CustomLoopRunStore store)
    {
        var admitted = CreateAdmittedRun();
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, admitted.LifecycleVersion)).Status);
        var terminal = Advance(running, CustomLoopRunStatus.Completed);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(terminal, running.LifecycleVersion)).Status);
        return terminal;
    }

    private static CustomLoopRunRecord CreateAdmittedRun()
    {
        var definition = CustomLoopDefinition.CreateSeed("loop-alpha", "default-role", "step-1", "create-loop", Timestamp);
        var admitted = Event(1, "event-1", CustomLoopRunEventKind.Admitted, Timestamp);
        var run = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, "run-alpha", definition.Id, 1, CustomLoopRunStatus.Admitted, Timestamp, Timestamp, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), "invoke-alpha", "test-user", string.Empty, definition, "Initial prompt", null, CustomLoopContextSnapshot.CreateEmpty(Timestamp), CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [admitted], null, null, null);
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static CustomLoopRunRecord Advance(CustomLoopRunRecord run, CustomLoopRunStatus status)
    {
        var updatedAt = run.UpdatedAtUtc.AddMinutes(1);
        var terminal = status is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview;
        return run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = status,
            UpdatedAtUtc = updatedAt,
            CompletedAtUtc = terminal ? updatedAt : null,
            ExecutionClock = status == CustomLoopRunStatus.Running ? new CustomLoopExecutionClock(run.ExecutionClock.AccumulatedRunningMilliseconds, updatedAt) : new CustomLoopExecutionClock(1_000, null),
            Events = [.. run.Events, Event(run.Events.Length + 1L, $"event-{run.Events.Length + 1}", CustomLoopRunEventKind.LifecycleChanged, updatedAt)],
            FinalOutput = status == CustomLoopRunStatus.Completed ? "done" : null
        };
    }

    private static CustomLoopRunEvent Event(long sequence, string id, CustomLoopRunEventKind kind, DateTimeOffset timestamp) => new(sequence, id, timestamp, kind, null, null, null, kind.ToString(), [], null, null, null, null, null, null, null, null, null, null);

    private static CustomLoopTraceDeletionRequest Request(string runId, string hash, string operationId = "delete-trace") => new(runId, hash, operationId, "actor-user", "web");

    private static CustomLoopTraceDeletionMutation Mutation(CustomLoopTraceDeletionRequest request) => new(request, CustomLoopTraceDeletionRequestHash.Compute(request), Timestamp.AddMinutes(3));

    private static CustomLoopTraceDeletionOperation PendingOperation(CustomLoopTraceDeletionMutation mutation) => new(CustomLoopTraceDeletionOperation.CurrentSchemaVersion, mutation.Request.OperationId, mutation.RequestHash, mutation.Request, mutation.RequestedAtUtc, mutation.RequestedAtUtc, CustomLoopTraceDeletionOperationState.PendingMutation, CustomLoopTraceDeletionStoreStatus.Unknown, null, CustomLoopTraceDeletionIntegrity.Unknown);

    private static async Task WriteOperationAsync(WorkspacePaths paths, CustomLoopTraceDeletionOperation operation)
    {
        Directory.CreateDirectory(paths.CustomLoopTraceDeletionOperationsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopTraceDeletionOperationsPath, operation.OperationId + ".json"), JsonSerializer.Serialize(operation, JsonOptions) + "\n");
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
