using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.TraceRetention.Models;
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
    private static readonly DateTimeOffset _timestamp = DateTimeOffset.Parse("2026-07-16T12:00:00+00:00");
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
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
        Assert.Equal(1, quota.DeletionOperationCount);
        Assert.Equal(CustomLoopLimits.MaxRunTraceDeletionOperationsPerWorkspace, quota.MaximumDeletionOperationCount);
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
    public async Task Reservation_selects_one_intent_owner_and_is_replayable_across_store_instances()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var firstStore = new CustomLoopRunStore(paths);
        var terminal = await CreateTerminalRunAsync(firstStore);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await firstStore.InspectTraceAsync(terminal.Id));
        var mutation = Mutation(Request(terminal.Id, inspection.PersistedArtifactHash));
        var secondStore = new CustomLoopRunStore(paths);

        var reservations = await Task.WhenAll(firstStore.ReserveTraceDeletionOperationAsync(mutation), secondStore.ReserveTraceDeletionOperationAsync(mutation));

        Assert.Single(reservations, result => result.Status == CustomLoopTraceDeletionReservationStatus.Reserved);
        Assert.Single(reservations, result => result.Status == CustomLoopTraceDeletionReservationStatus.Pending);
        Assert.All(reservations, result => Assert.Equal(CustomLoopTraceDeletionOperationState.PendingMutation, result.Operation!.State));
        Assert.Single(Directory.EnumerateFiles(paths.CustomLoopTraceDeletionOperationsPath, "*.json"));

        var deleted = await secondStore.DeleteTerminalTraceAsync(mutation);
        var replay = await firstStore.ReserveTraceDeletionOperationAsync(mutation);

        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, deleted.Status);
        Assert.Equal(CustomLoopTraceDeletionReservationStatus.OutcomeCommitted, replay.Status);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, replay.Operation!.Outcome);
    }

    [Fact]
    public async Task Reservation_treats_a_malformed_current_schema_discovery_index_as_repairable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var terminal = await CreateTerminalRunAsync(store);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(terminal.Id));
        var mutation = Mutation(Request(terminal.Id, inspection.PersistedArtifactHash));
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        await File.WriteAllTextAsync(indexPath, "{ malformed");

        var reservation = await store.ReserveTraceDeletionOperationAsync(mutation);
        var deletion = await store.DeleteTerminalTraceAsync(mutation);

        Assert.Equal(CustomLoopTraceDeletionReservationStatus.Reserved, reservation.Status);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, deletion.Status);
        Assert.Equal(1, JsonDocument.Parse(await File.ReadAllTextAsync(indexPath)).RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task Schema_change_after_reservation_preserves_the_pending_deletion_operation_for_exact_retry()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var terminal = await CreateTerminalRunAsync(store);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(terminal.Id));
        var mutation = Mutation(Request(terminal.Id, inspection.PersistedArtifactHash));
        Assert.Equal(CustomLoopTraceDeletionReservationStatus.Reserved, (await store.ReserveTraceDeletionOperationAsync(mutation)).Status);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        const string UnsupportedIndex = "{\"schemaVersion\":2,\"revision\":1,\"entries\":[]}";
        await File.WriteAllTextAsync(indexPath, UnsupportedIndex);

        var exception = await Assert.ThrowsAsync<UnsupportedCustomLoopRunDiscoveryIndexSchemaException>(() => store.DeleteTerminalTraceAsync(mutation));

        Assert.Contains("Delete `.custom-loop-run-index.json`", exception.Message, StringComparison.Ordinal);
        Assert.Equal(CustomLoopTraceDeletionLookupStatus.PendingMutation, (await store.GetTraceDeletionOperationAsync(mutation.Request.OperationId)).Status);
        Assert.False((await store.InspectTraceAsync(terminal.Id))!.IsDeleted);
        Assert.Equal(UnsupportedIndex, await File.ReadAllTextAsync(indexPath));

        File.Delete(indexPath);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(mutation)).Status);
    }

    [Fact]
    public async Task Reservation_uses_acquisition_time_and_does_not_decode_unrelated_run_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var reservedAtUtc = _timestamp.AddHours(1);
        var store = new CustomLoopRunStore(paths, new FixedTimeProvider(reservedAtUtc));
        var terminal = await CreateTerminalRunAsync(store);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(terminal.Id));
        var unrelatedLoopRoot = Path.Combine(paths.CustomLoopRunsPath, "loop-unrelated");
        Directory.CreateDirectory(unrelatedLoopRoot);
        await File.WriteAllTextAsync(Path.Combine(unrelatedLoopRoot, "run-unrelated.json"), "not-json");

        var reservation = await store.ReserveTraceDeletionOperationAsync(Mutation(Request(terminal.Id, inspection.PersistedArtifactHash)));

        Assert.Equal(CustomLoopTraceDeletionReservationStatus.Reserved, reservation.Status);
        Assert.Equal(_timestamp.AddMinutes(3), reservation.Operation!.RequestedAtUtc);
        Assert.Equal(reservedAtUtc, reservation.Operation.UpdatedAtUtc);
    }

    [Fact]
    public async Task Deletion_tombstone_uses_the_actual_post_intent_mutation_time()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var time = new MutableTimeProvider(_timestamp.AddHours(1));
        var store = new CustomLoopRunStore(paths, time);
        var terminal = await CreateTerminalRunAsync(store);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(terminal.Id));
        var mutation = Mutation(Request(terminal.Id, inspection.PersistedArtifactHash));
        Assert.Equal(CustomLoopTraceDeletionReservationStatus.Reserved, (await store.ReserveTraceDeletionOperationAsync(mutation)).Status);
        time.UtcNow = _timestamp.AddHours(2);

        var deleted = await store.DeleteTerminalTraceAsync(mutation);

        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, deleted.Status);
        Assert.Equal(time.UtcNow, deleted.Tombstone!.DeletedAtUtc);
        Assert.True(deleted.Tombstone.DeletedAtUtc > mutation.RequestedAtUtc);
    }

    [Fact]
    public async Task Intent_audit_failure_completion_is_durable_and_does_not_replace_trace_content()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var terminal = await CreateTerminalRunAsync(store);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(terminal.Id));
        var mutation = Mutation(Request(terminal.Id, inspection.PersistedArtifactHash));
        Assert.Equal(CustomLoopTraceDeletionReservationStatus.Reserved, (await store.ReserveTraceDeletionOperationAsync(mutation)).Status);

        var rejected = await store.CommitTraceDeletionAuditFailureAsync(mutation);
        var pendingAudit = await new CustomLoopRunStore(paths).GetTraceDeletionOperationAsync(mutation.Request.OperationId);

        Assert.Equal(CustomLoopTraceDeletionStoreStatus.AuditUnavailable, rejected.Status);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit, rejected.Integrity);
        Assert.Equal(CustomLoopTraceDeletionLookupStatus.OutcomeCommitted, pendingAudit.Status);
        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.Marked, await store.MarkTraceDeletionOutcomeAsync(mutation.Request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted));
        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.Marked, await store.MarkTraceDeletionOutcomeAsync(mutation.Request.OperationId, CustomLoopTraceDeletionIntegrity.Complete));

        var restarted = new CustomLoopRunStore(paths);
        var replay = await restarted.CommitTraceDeletionAuditFailureAsync(mutation);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.AuditUnavailable, replay.Status);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.Complete, replay.Integrity);
        Assert.Equal(CustomLoopTraceArtifactKind.LiveTrace, (await restarted.InspectTraceAsync(terminal.Id))!.Kind);
    }

    [Fact]
    public async Task Deletion_receipt_transitions_fail_closed_for_missing_conflicting_and_out_of_order_requests()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var missing = Mutation(Request("run-missing", new string('a', CustomLoopLimits.Sha256HexCharacters), "delete-missing"));
        var warning = Event(2, "event-warning", CustomLoopRunEventKind.IntegrityWarning, _timestamp.AddMinutes(1));

        Assert.Null(await store.GetMonitorAsync("run-missing"));
        Assert.Equal(CustomLoopTraceDeletionLookupStatus.NotFound, (await store.GetTraceDeletionOperationAsync("not-found")).Status);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Unknown, (await store.CommitTraceDeletionAuditFailureAsync(missing)).Status);
        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.NotFound, await store.MarkTraceDeletionOutcomeAsync("not-found", CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.MarkTraceDeletionOutcomeAsync("not-found", CustomLoopTraceDeletionIntegrity.Unknown));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AppendTerminalIntegrityWarningAsync("run-missing", 0, warning));
        Assert.Equal(CustomLoopRunStoreStatus.NotFound, (await store.AppendTerminalIntegrityWarningAsync("run-missing", 1, warning)).Status);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.HasSufficientTraceCapacityForDispatchAsync(CreateAdmittedRun(), 0));

        Assert.Equal(CustomLoopTraceDeletionReservationStatus.Reserved, (await store.ReserveTraceDeletionOperationAsync(missing)).Status);
        var conflict = Mutation(missing.Request with { Actor = "actor-other" });
        Assert.Equal(CustomLoopTraceDeletionReservationStatus.OperationConflict, (await store.ReserveTraceDeletionOperationAsync(conflict)).Status);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.OperationConflict, (await store.CommitTraceDeletionAuditFailureAsync(conflict)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkTraceDeletionOutcomeAsync(missing.Request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted));
        Assert.Null(await store.InspectTraceAsync("run-other"));

        var directMissing = Mutation(Request("run-other", new string('a', CustomLoopLimits.Sha256HexCharacters), "delete-direct-missing"));
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.NotFound, (await store.DeleteTerminalTraceAsync(directMissing)).Status);

        Assert.Equal(CustomLoopTraceDeletionStoreStatus.AuditUnavailable, (await store.CommitTraceDeletionAuditFailureAsync(missing)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkTraceDeletionOutcomeAsync(missing.Request.OperationId, CustomLoopTraceDeletionIntegrity.Complete));
        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.Marked, await store.MarkTraceDeletionOutcomeAsync(missing.Request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted));
        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.AlreadyMarked, await store.MarkTraceDeletionOutcomeAsync(missing.Request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted));
        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.Marked, await store.MarkTraceDeletionOutcomeAsync(missing.Request.OperationId, CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning));
        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.AlreadyMarked, await store.MarkTraceDeletionOutcomeAsync(missing.Request.OperationId, CustomLoopTraceDeletionIntegrity.Complete));
    }

    [Fact]
    public async Task Deletion_mutation_and_operation_readers_reject_invalid_authenticated_structure()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var mutation = Mutation(Request("run-missing", new string('a', CustomLoopLimits.Sha256HexCharacters), "delete-invalid"));

        await Assert.ThrowsAsync<ArgumentException>(() => store.ReserveTraceDeletionOperationAsync(mutation with { RequestHash = new string('b', CustomLoopLimits.Sha256HexCharacters) }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReserveTraceDeletionOperationAsync(Mutation(mutation.Request with { Actor = "invalid actor" })));
        Assert.Equal(CustomLoopTraceDeletionReservationStatus.Reserved, (await store.ReserveTraceDeletionOperationAsync(mutation)).Status);
        var pending = (await store.GetTraceDeletionOperationAsync(mutation.Request.OperationId)).Operation!;
        var invalid = new[]
        {
            pending with { SchemaVersion = 2 },
            pending with { OperationId = "delete-other" },
            pending with { UpdatedAtUtc = pending.RequestedAtUtc.AddTicks(-1) },
            pending with { Outcome = CustomLoopTraceDeletionStoreStatus.NotFound },
            pending with { State = CustomLoopTraceDeletionOperationState.OutcomeCommitted },
            pending with { State = CustomLoopTraceDeletionOperationState.OutcomeCommitted, Outcome = CustomLoopTraceDeletionStoreStatus.NotFound }
        };

        foreach (var candidate in invalid)
        {
            await WriteOperationAsync(paths, candidate, mutation.Request.OperationId);
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetTraceDeletionOperationAsync(mutation.Request.OperationId));
        }
    }

    [Fact]
    public async Task Tombstone_reader_rejects_invalid_terminal_size_time_actor_and_audit_structure()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var terminal = await CreateTerminalRunAsync(store);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(terminal.Id));
        var request = Request(terminal.Id, inspection.PersistedArtifactHash);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(Mutation(request))).Status);
        var operation = (await store.GetTraceDeletionOperationAsync(request.OperationId)).Operation!;
        var tombstone = operation.Tombstone!;
        var invalidTombstones = new[]
        {
            tombstone with { TerminalStatus = CustomLoopRunStatus.Running },
            tombstone with { DefinitionVersion = 0 },
            tombstone with { CompletedAtUtc = tombstone.CreatedAtUtc.AddTicks(-1) },
            tombstone with { DeletionActor = "invalid actor" },
            tombstone with { IntentAuditCorrelationId = "audit-other" }
        };

        foreach (var candidate in invalidTombstones)
        {
            await WriteOperationAsync(paths, operation with { Tombstone = candidate });
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetTraceDeletionOperationAsync(request.OperationId));
        }

        await WriteOperationAsync(paths, operation with { Outcome = CustomLoopTraceDeletionStoreStatus.AuditUnavailable });
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetTraceDeletionOperationAsync(request.OperationId));
    }

    [Fact]
    public async Task Rejected_operation_capacity_preserves_reserved_tombstone_deletions_and_remains_visible()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var terminal = await CreateTerminalRunAsync(store);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(terminal.Id));
        var generalOperationCapacity = CustomLoopLimits.MaxRunTraceDeletionOperationsPerWorkspace - CustomLoopLimits.ReservedRunTraceDeletionOperationsForTombstones;
        Directory.CreateDirectory(paths.CustomLoopTraceDeletionOperationsPath);
        await Parallel.ForEachAsync(Enumerable.Range(0, generalOperationCapacity), new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (index, cancellationToken) =>
        {
            var request = Request($"missing-{index:D5}", new string('a', CustomLoopLimits.Sha256HexCharacters), $"reject-{index:D5}");
            var mutation = Mutation(request);
            var operation = PendingOperation(mutation) with
            {
                State = CustomLoopTraceDeletionOperationState.OutcomeCommitted,
                Outcome = CustomLoopTraceDeletionStoreStatus.NotFound,
                Integrity = CustomLoopTraceDeletionIntegrity.Complete
            };
            await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopTraceDeletionOperationsPath, operation.OperationId + ".json"), JsonSerializer.Serialize(operation, _jsonOptions) + "\n", cancellationToken);
        });

        var quotaBefore = await store.GetTraceQuotaAsync();
        var deletionRequest = Request(terminal.Id, inspection.PersistedArtifactHash, "delete-reserved-trace");
        var deleted = await store.DeleteTerminalTraceAsync(Mutation(deletionRequest));
        var blockedRejection = await store.DeleteTerminalTraceAsync(Mutation(Request("missing-after-capacity", new string('a', CustomLoopLimits.Sha256HexCharacters), "reject-after-capacity")));
        var replay = await store.DeleteTerminalTraceAsync(Mutation(deletionRequest));
        var quotaAfter = await store.GetTraceQuotaAsync();

        Assert.Equal(generalOperationCapacity, quotaBefore.DeletionOperationCount);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, deleted.Status);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.DeletionOperationLimitExceeded, blockedRejection.Status);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.AlreadyDeleted, replay.Status);
        Assert.Equal(generalOperationCapacity + 1, quotaAfter.DeletionOperationCount);
        Assert.Equal(1, quotaAfter.TombstoneCount);
    }

    [Fact]
    public async Task Concurrent_trace_deletion_does_not_duplicate_a_cursor_item_and_filtered_refresh_discovers_its_tombstone()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var newer = await CreateTerminalRunAsync(store, "run-newer", "invoke-newer", _timestamp);
        var older = await CreateTerminalRunAsync(store, "run-older", "invoke-older", _timestamp.AddMinutes(-10));
        var first = await store.ListPageAsync(new CustomLoopRunPageRequest(1, newer.LoopId));
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(newer.Id));

        Assert.Equal(newer.Id, Assert.Single(first.Items).Id);
        Assert.NotNull(first.ContinuationCursor);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(Mutation(Request(newer.Id, inspection.PersistedArtifactHash)))).Status);
        var continuation = await store.ListPageAsync(new CustomLoopRunPageRequest(1, newer.LoopId, first.ContinuationCursor));
        var refreshed = await store.ListPageAsync(new CustomLoopRunPageRequest(1, newer.LoopId));

        Assert.Equal(older.Id, Assert.Single(continuation.Items).Id);
        Assert.DoesNotContain(newer.Id, continuation.Items.Select(item => item.Id));
        var tombstone = Assert.Single(refreshed.Items);
        Assert.Equal(newer.Id, tombstone.Id);
        Assert.True(tombstone.IsDeleted);
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
        Assert.Equal(CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit, nonterminal.Integrity);
        Assert.NotNull(await store.GetAsync(admitted.Id));

        var running = Advance(admitted, CustomLoopRunStatus.Running);
        await store.UpdateAsync(running, admitted.LifecycleVersion);
        var terminal = Advance(running, CustomLoopRunStatus.Completed);
        await store.UpdateAsync(terminal, running.LifecycleVersion);
        var mismatchRequest = Request(terminal.Id, new string('a', CustomLoopLimits.Sha256HexCharacters), "delete-other");
        var mismatch = await store.DeleteTerminalTraceAsync(Mutation(mismatchRequest));

        Assert.Equal(CustomLoopTraceDeletionStoreStatus.HashMismatch, mismatch.Status);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit, mismatch.Integrity);
        Assert.NotNull(await store.GetAsync(terminal.Id));
        Assert.Equal(CustomLoopTraceDeletionOperationState.OutcomeCommitted, (await store.GetTraceDeletionOperationAsync(mismatchRequest.OperationId)).Operation!.State);
    }

    [Fact]
    public async Task Deletion_rejects_default_request_and_persisted_operation_timestamps()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var terminal = await CreateTerminalRunAsync(store);
        var inspection = await store.InspectTraceAsync(terminal.Id);
        Assert.NotNull(inspection);
        var request = Request(terminal.Id, inspection.PersistedArtifactHash);

        await Assert.ThrowsAsync<FormatException>(() => store.DeleteTerminalTraceAsync(Mutation(request) with { RequestedAtUtc = default }));
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(Mutation(request))).Status);
        var operation = (await store.GetTraceDeletionOperationAsync(request.OperationId)).Operation;
        Assert.NotNull(operation);
        await WriteOperationAsync(paths, operation! with { RequestedAtUtc = default });

        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetTraceDeletionOperationAsync(request.OperationId));
    }

    [Fact]
    public async Task Pending_ledger_and_tombstone_first_crash_windows_recover_without_a_second_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var deletionTime = _timestamp.AddHours(1);
        var timeProvider = new FixedTimeProvider(deletionTime);
        var store = new CustomLoopRunStore(paths, timeProvider);
        var terminal = await CreateTerminalRunAsync(store);
        var inspection = await store.InspectTraceAsync(terminal.Id);
        Assert.NotNull(inspection);
        var request = Request(terminal.Id, inspection.PersistedArtifactHash);
        var mutation = Mutation(request);
        var pending = PendingOperation(mutation);
        await WriteOperationAsync(paths, pending);
        var retry = mutation with { RequestedAtUtc = mutation.RequestedAtUtc.AddMinutes(10) };

        var recoveredBeforeMutation = await new CustomLoopRunStore(paths, timeProvider).DeleteTerminalTraceAsync(retry);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, recoveredBeforeMutation.Status);
        var firstTombstone = (await store.InspectTraceAsync(terminal.Id))!.Tombstone;
        Assert.NotNull(firstTombstone);
        Assert.Equal(deletionTime, firstTombstone.DeletedAtUtc);

        await WriteOperationAsync(paths, pending);
        var recoveredAfterMutation = await new CustomLoopRunStore(paths, timeProvider).DeleteTerminalTraceAsync(retry);

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
        var tampered = JsonSerializer.Deserialize<Dictionary<string, object?>>(tombstone.GetRawText(), _jsonOptions)!;
        tampered["unknownField"] = true;
        await File.WriteAllTextAsync(tracePath, JsonSerializer.Serialize(tampered, _jsonOptions));

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
        var originalTombstone = operation!.Tombstone;
        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.Marked, await store.MarkTraceDeletionOutcomeAsync(conflictRequest.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted));
        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.Marked, await store.MarkTraceDeletionOutcomeAsync(conflictRequest.OperationId, CustomLoopTraceDeletionIntegrity.Complete));
        operation = (await store.GetTraceDeletionOperationAsync(conflictRequest.OperationId)).Operation;
        Assert.Equal(CustomLoopTraceDeletionIntegrity.Complete, operation!.Integrity);
        Assert.Equal(originalTombstone, operation.Tombstone);
        Assert.Equal(originalTombstone, (await store.InspectTraceAsync(terminal.Id))!.Tombstone);
        await WriteOperationAsync(paths, operation! with { Tombstone = operation.Tombstone! with { SchemaVersion = 99 } });

        var restarted = new CustomLoopRunStore(paths);
        await Assert.ThrowsAsync<FormatException>(() => restarted.GetTraceDeletionOperationAsync(conflictRequest.OperationId));
        await Assert.ThrowsAsync<FormatException>(() => restarted.DeleteTerminalTraceAsync(Mutation(conflictRequest)));

        await WriteOperationAsync(paths, operation with { Tombstone = operation.Tombstone with { RunId = "run-other" } });
        await Assert.ThrowsAsync<FormatException>(() => restarted.GetTraceDeletionOperationAsync(conflictRequest.OperationId));

        var rebound = operation.Tombstone with
        {
            DeletionOperationId = operation.OperationId,
            DeletionRequestHash = operation.RequestHash,
            IntentAuditCorrelationId = operation.OperationId,
            OutcomeAuditCorrelationId = operation.OperationId
        };
        await WriteOperationAsync(paths, operation with { Tombstone = rebound });
        await Assert.ThrowsAsync<FormatException>(() => restarted.GetTraceDeletionOperationAsync(conflictRequest.OperationId));
    }

    [Fact]
    public async Task Committed_operation_binds_request_derived_tombstone_metadata()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var terminal = await CreateTerminalRunAsync(store);
        var inspection = await store.InspectTraceAsync(terminal.Id);
        Assert.NotNull(inspection);
        var request = Request(terminal.Id, inspection.PersistedArtifactHash);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(Mutation(request))).Status);
        var operation = (await store.GetTraceDeletionOperationAsync(request.OperationId)).Operation;
        Assert.NotNull(operation?.Tombstone);
        var tombstone = operation!.Tombstone!;
        var tampered = new[]
        {
            tombstone with { RunId = "run-other" },
            tombstone with { OriginalTraceHash = new string('a', CustomLoopLimits.Sha256HexCharacters) },
            tombstone with { DeletionActor = "actor-other" },
            tombstone with { DeletionSurface = "cli" }
        };

        foreach (var candidate in tampered)
        {
            await WriteOperationAsync(paths, operation with { Tombstone = candidate });
            var restarted = new CustomLoopRunStore(paths);
            await Assert.ThrowsAsync<FormatException>(() => restarted.GetTraceDeletionOperationAsync(request.OperationId));
            await Assert.ThrowsAsync<FormatException>(() => restarted.DeleteTerminalTraceAsync(Mutation(request)));
        }
    }

    [Fact]
    public async Task Tombstone_reader_binds_deletion_metadata_to_its_request_hash()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var terminal = await CreateTerminalRunAsync(store);
        var inspection = await store.InspectTraceAsync(terminal.Id);
        Assert.NotNull(inspection);
        var request = Request(terminal.Id, inspection.PersistedArtifactHash);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(Mutation(request))).Status);
        var path = Path.Combine(paths.CustomLoopRunsPath, terminal.LoopId, terminal.Id + ".json");
        var tombstone = JsonSerializer.Deserialize<CustomLoopTraceTombstone>(await File.ReadAllTextAsync(path), _jsonOptions);
        Assert.NotNull(tombstone);
        var tampered = new[]
        {
            tombstone! with { OriginalTraceHash = new string('a', CustomLoopLimits.Sha256HexCharacters) },
            tombstone with { DeletionActor = "actor-other" },
            tombstone with { DeletionSurface = "cli" }
        };

        foreach (var candidate in tampered)
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(candidate, _jsonOptions) + "\n");
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).InspectTraceAsync(terminal.Id));
        }
    }

    private static async Task<CustomLoopRunRecord> CreateTerminalRunAsync(CustomLoopRunStore store, string runId = "run-alpha", string operationId = "invoke-alpha", DateTimeOffset? timestamp = null)
    {
        var admitted = CreateAdmittedRun(runId, operationId, timestamp);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, admitted.LifecycleVersion)).Status);
        var terminal = Advance(running, CustomLoopRunStatus.Completed);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(terminal, running.LifecycleVersion)).Status);
        return terminal;
    }

    private static CustomLoopRunRecord CreateAdmittedRun(string runId = "run-alpha", string operationId = "invoke-alpha", DateTimeOffset? timestamp = null)
    {
        var createdAt = timestamp ?? _timestamp;
        var definition = CustomLoopDefinition.CreateSeed("loop-alpha", "default-role", "step-1", "create-loop", createdAt);
        var admitted = Event(1, $"event-{runId}-1", CustomLoopRunEventKind.Admitted, createdAt);
        var run = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, runId, definition.Id, 1, CustomLoopRunStatus.Admitted, createdAt, createdAt, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), operationId, "test-user", string.Empty, definition, "Initial prompt", null, CustomLoopContextSnapshot.CreateEmpty(createdAt), CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [admitted], null, null, null) { CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, createdAt) };
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

    private static CustomLoopTraceDeletionMutation Mutation(CustomLoopTraceDeletionRequest request) => new(request, CustomLoopTraceDeletionRequestHash.Compute(request), _timestamp.AddMinutes(3));

    private static CustomLoopTraceDeletionOperation PendingOperation(CustomLoopTraceDeletionMutation mutation) => new(CustomLoopTraceDeletionOperation.CurrentSchemaVersion, mutation.Request.OperationId, mutation.RequestHash, mutation.Request, mutation.RequestedAtUtc, mutation.RequestedAtUtc, CustomLoopTraceDeletionOperationState.PendingMutation, CustomLoopTraceDeletionStoreStatus.Unknown, null, CustomLoopTraceDeletionIntegrity.Unknown);

    private static async Task WriteOperationAsync(WorkspacePaths paths, CustomLoopTraceDeletionOperation operation, string? fileOperationId = null)
    {
        Directory.CreateDirectory(paths.CustomLoopTraceDeletionOperationsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopTraceDeletionOperationsPath, (fileOperationId ?? operation.OperationId) + ".json"), JsonSerializer.Serialize(operation, _jsonOptions) + "\n");
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private sealed class MutableTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = timestamp;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
