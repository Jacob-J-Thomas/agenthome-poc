using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopRunStoreTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-16T12:00:00+00:00");
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    [Fact]
    public async Task Create_round_trips_from_the_custom_run_directory_after_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        var result = await new CustomLoopRunStore(paths).CreateAsync(run);

        Assert.Equal(CustomLoopRunStoreStatus.Created, result.Status);
        Assert.Same(run, result.Run);
        var path = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json");
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, run.Id + ".json")));
        var json = await File.ReadAllTextAsync(path);
        Assert.StartsWith("{\"artifactKind\":\"custom-loop-run\",\"artifactSchemaVersion\":1,\"projectionSchemaVersion\":1,\"encoding\":\"utf-8\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"admitted\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("isTerminal", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.EndsWith("\n", json, StringComparison.Ordinal);

        var restarted = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        AssertRun(run, await restarted.GetAsync(run.Id));
        AssertRun(run, await restarted.GetByAdmissionOperationAsync(run.AdmissionOperationId));
        var summary = Assert.Single(await restarted.ListRecentAsync(50));
        Assert.Equal(run.Id, summary.Id);
        Assert.Equal(run.AdmittedDefinition.DefinitionVersion, summary.DefinitionVersion);
        Assert.False(summary.IsDeleted);
        Assert.Equal(run.Id, Assert.Single(await restarted.ListNonterminalAsync()).Id);
    }

    [Fact]
    public async Task Canonical_envelope_reprojects_seedlessly_when_later_checkpoint_content_precedes_prior_event_content_after_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun();
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);

        var running = Advance(admitted, CustomLoopRunStatus.Running, "event-prior-content");
        running = running with { Events = [.. running.Events[..^1], running.Events[^1] with { Detail = "Prior event-only content survives the later checkpoint update." }] };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, admitted.LifecycleVersion)).Status);

        var restarted = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var prior = (await restarted.GetAsync(admitted.Id))!;
        var checkpointContent = "Later checkpoint content is encountered before the immutable prior event during canonical projection.";
        var retained = new CustomLoopRetainedOutput("step-1", 1, checkpointContent, CustomLoopTraceContentHash.Compute(checkpointContent));
        var pauseRequested = Advance(prior, CustomLoopRunStatus.PauseRequested, "event-later-checkpoint");
        pauseRequested = pauseRequested with { Checkpoint = pauseRequested.Checkpoint with { CurrentIterationResult = retained } };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(prior, pauseRequested).IsValid);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await restarted.UpdateAsync(pauseRequested, prior.LifecycleVersion)).Status);

        var restartedAgain = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var persisted = (await restartedAgain.GetAsync(admitted.Id))!;
        Assert.Equal(checkpointContent, persisted.Checkpoint.CurrentIterationResult!.Content);
        Assert.Equal("Prior event-only content survives the later checkpoint update.", persisted.Events[^2].Detail);
        var path = Path.Combine(paths.CustomLoopRunsPath, admitted.LoopId, admitted.Id + ".json");
        Assert.Equal(CustomLoopRunArtifactSerializer.Serialize(persisted), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Empty_store_reads_are_restart_safe_and_non_mutating()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);

        Assert.Null(await store.GetAsync("run-missing"));
        Assert.Null(await store.GetByAdmissionOperationAsync("invoke-missing"));
        Assert.Null(await store.GetNonterminalByLoopAsync("loop-missing"));
        Assert.Empty(await store.ListRecentAsync(50));
        Assert.Empty(await store.ListNonterminalAsync());
        Assert.Equal(CustomLoopTraceQuota.Empty(), await store.GetTraceQuotaAsync());
        Assert.False(Directory.Exists(paths.CustomLoopRunsPath));
    }

    [Fact]
    public async Task Trace_quota_reserves_the_maximum_for_nonterminal_runs_and_commits_actual_bytes_at_terminalization()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun();
        await store.CreateAsync(admitted);

        var active = await store.GetTraceQuotaAsync();

        Assert.Equal(1, active.RetainedTraceCount);
        Assert.Equal(1, active.ActiveReservationCount);
        Assert.Equal(CustomLoopLimits.MaxRunTraceUtf8Bytes, active.AccountedTraceUtf8Bytes);
        Assert.Equal(CustomLoopLimits.MaxRunTraceUtf8Bytes - active.ActualTraceUtf8Bytes, active.ReservedCapacityUtf8Bytes);
        Assert.Equal(CustomLoopLimits.MaxRunTraceWorkspaceUtf8Bytes - CustomLoopLimits.MaxRunTraceUtf8Bytes, active.AvailableAccountedUtf8Bytes);
        Assert.False(active.IsOverLimit);

        var running = Advance(admitted, CustomLoopRunStatus.Running);
        await store.UpdateAsync(running, admitted.LifecycleVersion);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        await store.UpdateAsync(completed, running.LifecycleVersion);
        var terminal = await store.GetTraceQuotaAsync();
        var path = Path.Combine(paths.CustomLoopRunsPath, admitted.LoopId, admitted.Id + ".json");

        Assert.Equal(1, terminal.ActiveReservationCount);
        Assert.Equal(new FileInfo(path).Length, terminal.ActualTraceUtf8Bytes);
        Assert.Equal(terminal.ActualTraceUtf8Bytes + CustomLoopLimits.MaxTraceControlEventUtf8Bytes, terminal.AccountedTraceUtf8Bytes);
        Assert.Equal(CustomLoopLimits.MaxTraceControlEventUtf8Bytes, terminal.ReservedCapacityUtf8Bytes);
    }

    [Fact]
    public async Task Trace_quota_reserves_inference_step_named_exit_and_the_real_Exit_as_distinct_attempt_shapes()
    {
        using var collisionWorkspace = new TestWorkspace();
        using var controlWorkspace = new TestWorkspace();
        var collisionStore = new CustomLoopRunStore(new WorkspacePaths(collisionWorkspace.RootPath));
        var controlStore = new CustomLoopRunStore(new WorkspacePaths(controlWorkspace.RootPath));
        var collision = WithRepeatingStepId(CreateRun(), "exit");
        var control = WithRepeatingStepId(CreateRun(), "work");

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await collisionStore.CreateAsync(collision)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await controlStore.CreateAsync(control)).Status);
        var collisionQuota = await collisionStore.GetTraceQuotaAsync();
        var controlQuota = await controlStore.GetTraceQuotaAsync();

        Assert.Equal(controlQuota.AccountedTraceUtf8Bytes, collisionQuota.AccountedTraceUtf8Bytes);
        Assert.Equal(controlQuota.ReservedCapacityUtf8Bytes, collisionQuota.ReservedCapacityUtf8Bytes);
    }

    [Fact]
    public async Task Create_requires_initial_lifecycle_version_and_admitted_status()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var run = CreateRun();

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(run with { LifecycleVersion = 2 }));
        var running = run with { Status = CustomLoopRunStatus.Running, ExecutionClock = new CustomLoopExecutionClock(0, Timestamp) };
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(running));
    }

    [Fact]
    public async Task Create_atomically_replays_matching_operation_and_rejects_changed_operation_or_run_identity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var firstStore = new CustomLoopRunStore(paths);
        var secondStore = new CustomLoopRunStore(paths);
        var original = CreateRun();
        await firstStore.CreateAsync(original);
        var replayCandidate = CreateRun(runId: "run-replay");
        var changedRequest = CustomLoopAdmissionRequestHash.Apply(replayCandidate with { TriggerPrompt = "Different invocation" });
        var runIdCollision = CreateRun(loopId: "loop-beta", runId: original.Id, operationId: "invoke-beta");

        var replay = await secondStore.CreateAsync(replayCandidate);
        var operationConflict = await secondStore.CreateAsync(changedRequest);
        var identityConflict = await secondStore.CreateAsync(runIdCollision);

        Assert.Equal(CustomLoopRunStoreStatus.AlreadyCreated, replay.Status);
        AssertRun(original, replay.Run);
        Assert.Equal(CustomLoopRunStoreStatus.OperationConflict, operationConflict.Status);
        Assert.Equal(original.Id, operationConflict.Conflict!.RunId);
        Assert.Equal(CustomLoopRunStoreStatus.Conflict, identityConflict.Status);
        Assert.Equal(0, identityConflict.Conflict!.ExpectedLifecycleVersion);
        Assert.Null(await secondStore.GetAsync(replayCandidate.Id));
        Assert.Equal(original.LoopId, (await secondStore.GetAsync(original.Id))!.LoopId);
    }

    [Fact]
    public async Task Create_rejects_a_second_nonterminal_run_but_allows_one_after_terminalization()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var first = CreateRun();
        var second = CreateRun(runId: "run-beta", operationId: "invoke-beta");
        await store.CreateAsync(first);

        var activeConflict = await store.CreateAsync(second);

        Assert.Equal(CustomLoopRunStoreStatus.NonterminalRunExists, activeConflict.Status);
        Assert.Equal(first.Id, activeConflict.Run!.Id);
        Assert.Equal(first.Id, (await store.GetNonterminalByLoopAsync(first.LoopId))!.Id);
        var running = Advance(first, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, 1)).Status);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(completed, 2)).Status);
        Assert.Null(await store.GetNonterminalByLoopAsync(first.LoopId));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(second)).Status);
    }

    [Fact]
    public async Task Concurrent_expected_version_updates_allow_exactly_one_writer()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var firstStore = new CustomLoopRunStore(paths);
        var secondStore = new CustomLoopRunStore(paths);
        var admitted = CreateRun();
        await firstStore.CreateAsync(admitted);
        var first = Advance(admitted, CustomLoopRunStatus.Running, "event-first");
        var second = Advance(admitted, CustomLoopRunStatus.Running, "event-second");

        var results = await Task.WhenAll(firstStore.UpdateAsync(first, 1), secondStore.UpdateAsync(second, 1));

        Assert.Single(results, result => result.Status == CustomLoopRunStoreStatus.Updated);
        var conflict = Assert.Single(results, result => result.Status == CustomLoopRunStoreStatus.Conflict);
        Assert.Equal(1, conflict.Conflict!.ExpectedLifecycleVersion);
        Assert.Equal(2, conflict.Conflict.ActualLifecycleVersion);
        Assert.Equal(CustomLoopRunStatus.Running, conflict.Conflict.ActualStatus);
        Assert.Contains((await firstStore.GetAsync(admitted.Id))!.Events[1].EventId, new[] { "event-first", "event-second" });
    }

    [Fact]
    public async Task Os_exclusive_lock_serializes_mutation_and_cancellation_releases_the_process_gate()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun();
        await store.CreateAsync(admitted);
        var candidate = Advance(admitted, CustomLoopRunStatus.Running);
        var lockPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-runs.lock");

        using (var externalLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateAsync(candidate, 1, cancellation.Token));
        }

        var result = await store.UpdateAsync(candidate, 1);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, result.Status);
    }

    [Fact]
    public async Task Update_returns_missing_stale_and_terminal_results_without_overwrite()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var admitted = CreateRun();
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.NotFound, (await store.UpdateAsync(running, 1)).Status);
        await store.CreateAsync(admitted);
        await store.UpdateAsync(running, 1);

        var stale = await store.UpdateAsync(running, 1);
        Assert.Equal(CustomLoopRunStoreStatus.Conflict, stale.Status);
        Assert.Equal(2, stale.Conflict!.ActualLifecycleVersion);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        await store.UpdateAsync(completed, 2);
        var afterTerminal = completed with { LifecycleVersion = 4, UpdatedAtUtc = completed.UpdatedAtUtc.AddMinutes(1), CompletedAtUtc = completed.CompletedAtUtc!.Value.AddMinutes(1) };

        var terminal = await store.UpdateAsync(afterTerminal, 3);

        Assert.Equal(CustomLoopRunStoreStatus.TerminalImmutable, terminal.Status);
        Assert.Equal(3, (await store.GetAsync(completed.Id))!.LifecycleVersion);
    }

    [Fact]
    public async Task Terminal_integrity_warning_append_is_one_time_CAS_idempotent_and_preserves_the_terminal_outcome_and_event_prefix()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var admitted = CreateRun();
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        await store.CreateAsync(admitted);
        await store.UpdateAsync(running, admitted.LifecycleVersion);
        await store.UpdateAsync(completed, running.LifecycleVersion);
        var prefixJson = JsonSerializer.Serialize(completed.Events);
        var warning = Event(completed.Events.Length + 1L, "event-terminal-audit-warning", CustomLoopRunEventKind.IntegrityWarning, completed.UpdatedAtUtc.AddMinutes(1)) with { Detail = "Terminal audit append failed after the truthful trace became durable." };

        var appended = await store.AppendTerminalIntegrityWarningAsync(completed.Id, completed.LifecycleVersion, warning);
        var replayed = await store.AppendTerminalIntegrityWarningAsync(completed.Id, completed.LifecycleVersion, warning);
        var persisted = (await store.GetAsync(completed.Id))!;

        Assert.Equal(CustomLoopRunStoreStatus.Updated, appended.Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, replayed.Status);
        Assert.Equal(completed.LifecycleVersion + 1, persisted.LifecycleVersion);
        Assert.Equal(completed.Status, persisted.Status);
        Assert.Equal(completed.CompletedAtUtc, persisted.CompletedAtUtc);
        Assert.Equal(completed.FinalOutput, persisted.FinalOutput);
        Assert.Equal(completed.FailureCode, persisted.FailureCode);
        Assert.Equal(completed.FailureDetail, persisted.FailureDetail);
        Assert.Equal(prefixJson, JsonSerializer.Serialize(persisted.Events.Take(completed.Events.Length)));
        Assert.Equal(warning.EventId, persisted.Events[^1].EventId);
        Assert.Equal(CustomLoopRunStoreStatus.Conflict, (await store.AppendTerminalIntegrityWarningAsync(completed.Id, completed.LifecycleVersion - 1, warning)).Status);
        var second = warning with { Sequence = warning.Sequence + 1, EventId = "event-second-terminal-warning", TimestampUtc = warning.TimestampUtc.AddMinutes(1) };
        await Assert.ThrowsAsync<FormatException>(() => store.AppendTerminalIntegrityWarningAsync(completed.Id, persisted.LifecycleVersion, second));
    }

    [Fact]
    public async Task Lifecycle_control_capacity_preserves_terminal_and_warning_slots_across_restart_at_the_exact_boundary()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var current = CreateRun();
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(current)).Status);

        for (var count = 1; count <= CustomLoopLimits.MaxNonterminalLifecycleControlEventsPerRun; count++)
        {
            var nextStatus = (count % 3) switch
            {
                1 => CustomLoopRunStatus.Running,
                2 => CustomLoopRunStatus.PauseRequested,
                _ => CustomLoopRunStatus.Paused
            };
            var candidate = Advance(current, nextStatus);
            var updated = await store.UpdateAsync(candidate, current.LifecycleVersion);
            Assert.Equal(CustomLoopRunStoreStatus.Updated, updated.Status);
            current = updated.Run!;
        }

        Assert.Equal(CustomLoopLimits.MaxNonterminalLifecycleControlEventsPerRun, current.Events.Count(IsLifecycleControlEvent));
        var exhaustedNonterminal = Advance(current, CustomLoopRunStatus.Paused);
        await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(exhaustedNonterminal, current.LifecycleVersion));
        Assert.Equal(current.LifecycleVersion, (await store.GetAsync(current.Id))!.LifecycleVersion);

        var completed = Advance(current, CustomLoopRunStatus.Completed);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(completed, current.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopLimits.MaxTerminalLifecycleControlEventsBeforeIntegrityWarning, completed.Events.Count(IsLifecycleControlEvent));

        var restarted = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var terminalQuota = await restarted.GetTraceQuotaAsync();
        Assert.Equal(1, terminalQuota.ActiveReservationCount);
        Assert.Equal(CustomLoopLimits.MaxTraceControlEventUtf8Bytes, terminalQuota.ReservedCapacityUtf8Bytes);
        Assert.Equal(terminalQuota.ActualTraceUtf8Bytes + CustomLoopLimits.MaxTraceControlEventUtf8Bytes, terminalQuota.AccountedTraceUtf8Bytes);

        var warning = Event(completed.Events.Length + 1L, "event-terminal-boundary-warning", CustomLoopRunEventKind.IntegrityWarning, completed.UpdatedAtUtc.AddMinutes(1)) with { Detail = "The durable terminal audit append failed after terminalization." };
        var appended = await restarted.AppendTerminalIntegrityWarningAsync(completed.Id, completed.LifecycleVersion, warning);
        var replayed = await restarted.AppendTerminalIntegrityWarningAsync(completed.Id, completed.LifecycleVersion, warning);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, appended.Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, replayed.Status);

        var restartedAfterWarning = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var persisted = (await restartedAfterWarning.GetAsync(completed.Id))!;
        Assert.Equal(CustomLoopLimits.MaxLifecycleControlEventsPerRun, persisted.Events.Count(IsLifecycleControlEvent));
        Assert.Equal(completed.Status, persisted.Status);
        Assert.Equal(completed.CompletedAtUtc, persisted.CompletedAtUtc);
        Assert.Equal(completed.FinalOutput, persisted.FinalOutput);
        Assert.Equal(JsonSerializer.Serialize(completed.Events), JsonSerializer.Serialize(persisted.Events.Take(completed.Events.Length)));
        Assert.Equal(warning.EventId, persisted.Events[^1].EventId);
        var warningQuota = await restartedAfterWarning.GetTraceQuotaAsync();
        Assert.Equal(0, warningQuota.ActiveReservationCount);
        Assert.Equal(0, warningQuota.ReservedCapacityUtf8Bytes);
        Assert.Equal(warningQuota.ActualTraceUtf8Bytes, warningQuota.AccountedTraceUtf8Bytes);
        var second = warning with { Sequence = warning.Sequence + 1, EventId = "event-second-boundary-warning", TimestampUtc = warning.TimestampUtc.AddMinutes(1) };
        await Assert.ThrowsAsync<FormatException>(() => restartedAfterWarning.AppendTerminalIntegrityWarningAsync(completed.Id, persisted.LifecycleVersion, second));
    }

    [Fact]
    public async Task Update_rejects_non_successor_invalid_transition_and_admitted_snapshot_mutation()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var admitted = CreateRun();
        await store.CreateAsync(admitted);

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateAsync(Advance(admitted, CustomLoopRunStatus.Running) with { LifecycleVersion = 4 }, 1));
        var invalidTransition = Advance(admitted, CustomLoopRunStatus.PauseRequested);
        await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(invalidTransition, 1));
        var changedSnapshot = Advance(admitted, CustomLoopRunStatus.Running) with
        {
            ContextSnapshot = admitted.ContextSnapshot with { ManifestHash = CustomLoopTraceContentHash.Compute("changed") }
        };
        await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(changedSnapshot, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.UpdateAsync(Advance(admitted, CustomLoopRunStatus.Running) with { LifecycleVersion = 1 }, 0));
        Assert.Equal(CustomLoopRunStatus.Admitted, (await store.GetAsync(admitted.Id))!.Status);
    }

    [Fact]
    public async Task ListRecent_is_bounded_and_orders_durable_summaries_deterministically()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteDirectAsync(paths, CreateRun("loop-alpha", "run-alpha", "invoke-alpha"));
        var recent = CreateRun("loop-beta", "run-beta", "invoke-beta") with { CreatedAtUtc = Timestamp.AddMinutes(1), UpdatedAtUtc = Timestamp.AddMinutes(2) };
        recent = recent with { ContextSnapshot = CustomLoopContextSnapshot.CreateEmpty(Timestamp.AddMinutes(1)), Events = [recent.Events[0] with { TimestampUtc = Timestamp.AddMinutes(1) }] };
        recent = CustomLoopAdmissionRequestHash.Apply(recent);
        await WriteDirectAsync(paths, recent);
        var store = new CustomLoopRunStore(paths);

        var one = await store.ListRecentAsync(1);
        var all = await store.ListRecentAsync(50);

        Assert.Equal("run-beta", Assert.Single(one).Id);
        Assert.Equal(new[] { "run-beta", "run-alpha" }, all.Select(summary => summary.Id));
        Assert.All(all, summary => Assert.False(summary.IsDeleted));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListRecentAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListRecentAsync(CustomLoopLimits.MaxRecentRunsPageSize + 1));
    }

    [Fact]
    public async Task Strict_reader_rejects_missing_unknown_and_noncanonical_nested_properties_or_enums()
    {
        var mutations = new Action<JsonObject>[]
        {
            root => root.Remove("surface"),
            root => root["unknownField"] = true,
            root => ((JsonObject)root["admittedDefinition"]!["triggerPolicy"]!).Remove("includeInvokingConversation"),
            root => ((JsonObject)root["contextSnapshot"]!)["unknownNested"] = 1,
            root => root["status"] = "Admitted",
            root => root["events"] = new JsonObject(),
            root => root["contextSnapshot"] = "not-an-object"
        };

        foreach (var mutate in mutations)
        {
            using var workspace = new TestWorkspace();
            var paths = new WorkspacePaths(workspace.RootPath);
            var run = CreateRun();
            var root = JsonNode.Parse(JsonSerializer.Serialize(run, ArtifactJsonOptions))!.AsObject();
            mutate(root);
            await WriteRawAsync(paths, run.LoopId, run.Id, root.ToJsonString(ArtifactJsonOptions));

            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync(run.Id));
        }
    }

    [Fact]
    public async Task Strict_reader_rejects_duplicate_properties_invalid_json_and_unsupported_schema()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        var json = JsonSerializer.Serialize(run, ArtifactJsonOptions);
        var schemaProperty = $"\"schemaVersion\": {CustomLoopRunRecord.CurrentSchemaVersion}";
        var duplicate = json.Replace(schemaProperty, schemaProperty + ",\n  " + schemaProperty, StringComparison.Ordinal);
        await WriteRawAsync(paths, run.LoopId, run.Id, duplicate);
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync(run.Id));

        await WriteRawAsync(paths, run.LoopId, run.Id, "{invalid");
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync(run.Id));

        await WriteRawAsync(paths, run.LoopId, run.Id, JsonSerializer.Serialize(run with { SchemaVersion = 99 }, ArtifactJsonOptions));
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync(run.Id));

        await WriteRawAsync(paths, run.LoopId, run.Id, JsonSerializer.Serialize(run with { SchemaVersion = 1 }, ArtifactJsonOptions));
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync(run.Id));
    }

    [Fact]
    public async Task Reader_rejects_oversize_tampered_identity_and_duplicate_global_ids()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var oversizedPath = await WriteRawAsync(paths, "loop-alpha", "run-alpha", string.Empty);
        await File.WriteAllBytesAsync(oversizedPath, new byte[CustomLoopLimits.MaxRunTraceUtf8Bytes + 1]);
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync("run-alpha"));

        File.Delete(oversizedPath);
        await WriteRawAsync(paths, "loop-other", "run-alpha", JsonSerializer.Serialize(CreateRun(), ArtifactJsonOptions));
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync("run-alpha"));

        Directory.Delete(Path.Combine(paths.CustomLoopRunsPath, "loop-other"), recursive: true);
        var first = CreateRun();
        await WriteDirectAsync(paths, first);
        var duplicate = CreateRun("loop-beta", first.Id, "invoke-beta");
        await WriteDirectAsync(paths, duplicate);
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync(first.Id));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("UPPERCASE")]
    [InlineData("has space")]
    [InlineData("con")]
    [InlineData("trailing-")]
    public async Task Public_identity_reads_reject_unsafe_values(string value)
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(value));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetByAdmissionOperationAsync(value));
    }

    [Fact]
    public async Task Reparse_point_in_the_artifact_hierarchy_fails_closed_when_supported()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var outside = Path.Combine(workspace.RootPath, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.CustomLoopRunsPath)!);
        try
        {
            Directory.CreateSymbolicLink(paths.CustomLoopRunsPath, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        await Assert.ThrowsAsync<IOException>(() => new CustomLoopRunStore(paths).ListRecentAsync(1));
    }

    [Fact]
    public async Task Create_enforces_250_trace_limit_without_pruning_existing_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        for (var index = 0; index < CustomLoopLimits.MaxRunTracesPerWorkspace; index++)
        {
            await WriteDirectAsync(paths, CreateRun($"loop-{index:D3}", $"run-{index:D3}", $"invoke-{index:D3}"));
        }

        var extra = CreateRun("loop-extra", "run-extra", "invoke-extra");
        var result = await new CustomLoopRunStore(paths).CreateAsync(extra);

        Assert.Equal(CustomLoopRunStoreStatus.LimitExceeded, result.Status);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, extra.LoopId, extra.Id + ".json")));
        Assert.Equal(CustomLoopLimits.MaxRunTracesPerWorkspace, Directory.EnumerateFiles(paths.CustomLoopRunsPath, "*.json", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task Create_enforces_the_restart_derived_one_gibibyte_reservation_without_allocating_sparse_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var maximumReservations = checked((int)(CustomLoopLimits.MaxRunTraceWorkspaceUtf8Bytes / CustomLoopLimits.MaxRunTraceUtf8Bytes));
        for (var index = 0; index < maximumReservations; index++)
        {
            await WriteDirectAsync(paths, CreateRun($"loop-{index:D3}", $"run-{index:D3}", $"invoke-{index:D3}"));
        }

        var store = new CustomLoopRunStore(paths);
        var quota = await store.GetTraceQuotaAsync();
        var extra = CreateRun("loop-extra", "run-extra", "invoke-extra");
        var result = await store.CreateAsync(extra);

        Assert.Equal(maximumReservations, quota.ActiveReservationCount);
        Assert.Equal(CustomLoopLimits.MaxRunTraceWorkspaceUtf8Bytes, quota.AccountedTraceUtf8Bytes);
        Assert.Equal(0, quota.AvailableAccountedUtf8Bytes);
        Assert.Equal(CustomLoopRunStoreStatus.LimitExceeded, result.Status);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, extra.LoopId, extra.Id + ".json")));
        Assert.Equal(maximumReservations, Directory.EnumerateFiles(paths.CustomLoopRunsPath, "*.json", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task GetByAdmissionOperation_fails_closed_on_duplicate_persisted_operations()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteDirectAsync(paths, CreateRun());
        await WriteDirectAsync(paths, CreateRun("loop-beta", "run-beta", "invoke-alpha"));

        var store = new CustomLoopRunStore(paths);
        await Assert.ThrowsAsync<FormatException>(() => store.GetByAdmissionOperationAsync("invoke-alpha"));
        await Assert.ThrowsAsync<FormatException>(() => store.ListRecentAsync(50));
    }

    [Fact]
    public async Task Corrupt_multiple_nonterminal_runs_fail_closed_for_lookup_and_new_admission()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteDirectAsync(paths, CreateRun());
        await WriteDirectAsync(paths, CreateRun(runId: "run-beta", operationId: "invoke-beta"));
        var store = new CustomLoopRunStore(paths);

        await Assert.ThrowsAsync<FormatException>(() => store.GetNonterminalByLoopAsync("loop-alpha"));
        await Assert.ThrowsAsync<FormatException>(() => store.CreateAsync(CreateRun(runId: "run-gamma", operationId: "invoke-gamma")));
    }

    [Fact]
    public async Task Update_fails_closed_when_run_id_is_duplicated_across_loop_directories()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = CreateRun();
        await WriteDirectAsync(paths, first);
        await WriteDirectAsync(paths, CreateRun("loop-beta", first.Id, "invoke-beta"));

        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).UpdateAsync(Advance(first, CustomLoopRunStatus.Running), 1));
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).ListRecentAsync(50));
    }

    [Fact]
    public async Task Corrupt_layout_names_and_workspace_trace_overflow_fail_closed_without_pruning()
    {
        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            Directory.CreateDirectory(paths.CustomLoopRunsPath);
            await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopRunsPath, "misplaced.json"), "{}");
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).ListRecentAsync(50));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            Directory.CreateDirectory(Path.Combine(paths.CustomLoopRunsPath, "Unsafe Directory"));
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).ListRecentAsync(50));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var directory = Path.Combine(paths.CustomLoopRunsPath, "loop-alpha");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "Unsafe Run.json"), "{}");
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).ListRecentAsync(50));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            for (var index = 0; index <= CustomLoopLimits.MaxRunTracesPerWorkspace; index++)
            {
                var directory = Path.Combine(paths.CustomLoopRunsPath, $"loop-{index:D3}");
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, $"run-{index:D3}.json"), "{}");
            }

            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).ListRecentAsync(50));
            Assert.Equal(CustomLoopLimits.MaxRunTracesPerWorkspace + 1, Directory.EnumerateFiles(paths.CustomLoopRunsPath, "*.json", SearchOption.AllDirectories).Count());
        }
    }

    [Fact]
    public async Task Create_rejects_trace_that_cannot_fit_the_bounded_artifact_before_writing()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        var blocks = Enumerable.Range(0, 66).Select(index =>
        {
            var prefix = index.ToString("D2") + ":";
            var content = prefix + new string('x', CustomLoopLimits.MaxLogicalProviderRequestCharacters - prefix.Length);
            return new CustomLoopContextBlock(CustomLoopContextSource.HarnessGovernance, $"source-{index}", LlmMessageRole.System, true, null, content, CustomLoopTraceContentHash.Compute(content), content.Length, false, EmbodySenseDeveloperInstructions.CurrentVersion);
        }).ToArray();
        run = run with { Events = [run.Events[0] with { ContextBlocks = blocks }] };

        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).CreateAsync(run));
        Assert.False(Directory.Exists(paths.CustomLoopRunsPath));
    }

    [Fact]
    public async Task Artifact_directory_occupied_by_file_fails_without_writing_elsewhere()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await File.WriteAllTextAsync(paths.AgentPath, "occupied");

        await Assert.ThrowsAsync<IOException>(() => new CustomLoopRunStore(paths).CreateAsync(CreateRun()));
        Assert.True(File.Exists(paths.AgentPath));
    }

    private static CustomLoopRunRecord CreateRun(string loopId = "loop-alpha", string runId = "run-alpha", string operationId = "invoke-alpha")
    {
        var definition = CustomLoopDefinition.CreateSeed(loopId, "default-role", "step-1", "create-loop", Timestamp);
        var context = CustomLoopContextSnapshot.CreateEmpty(Timestamp);
        var admitted = Event(1, "event-1", CustomLoopRunEventKind.Admitted, Timestamp);
        var run = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, runId, loopId, 1, CustomLoopRunStatus.Admitted, Timestamp, Timestamp, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), operationId, string.Empty, definition, "Initial prompt", null, context, CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [admitted], null, null, null);
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static CustomLoopRunRecord Advance(CustomLoopRunRecord run, CustomLoopRunStatus status, string? eventId = null)
    {
        var updatedAt = run.UpdatedAtUtc.AddMinutes(1);
        var terminal = status is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview;
        var lifecycle = Event(run.Events.Length + 1L, eventId ?? $"event-{run.Events.Length + 1}", CustomLoopRunEventKind.LifecycleChanged, updatedAt);
        return run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = status,
            UpdatedAtUtc = updatedAt,
            CompletedAtUtc = terminal ? updatedAt : null,
            ExecutionClock = status is CustomLoopRunStatus.Running or CustomLoopRunStatus.PauseRequested
                ? new CustomLoopExecutionClock(run.ExecutionClock.AccumulatedRunningMilliseconds, updatedAt)
                : new CustomLoopExecutionClock(run.ExecutionClock.AccumulatedRunningMilliseconds + (run.ExecutionClock.ActiveSinceUtc is null ? 0 : 1_000), null),
            Events = [.. run.Events, lifecycle],
            FinalOutput = status == CustomLoopRunStatus.Completed ? "done" : null,
            FailureCode = status is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview ? "failure" : null,
            FailureDetail = status is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview ? "Safe failure detail" : null
        };
    }

    private static CustomLoopRunRecord WithRepeatingStepId(CustomLoopRunRecord run, string stepId)
    {
        var definition = run.AdmittedDefinition with
        {
            InferenceSteps = [run.AdmittedDefinition.InferenceSteps.Single() with { Id = stepId }],
            ExitPolicy = run.AdmittedDefinition.ExitPolicy with { MaxAdditionalIterations = 1 }
        };
        definition = CustomLoopDefinitionContentHash.Apply(definition with { ContentHash = string.Empty });
        return CustomLoopAdmissionRequestHash.Apply(run with { AdmittedDefinition = definition, AdmissionRequestHash = string.Empty });
    }

    private static CustomLoopRunEvent Event(long sequence, string eventId, CustomLoopRunEventKind kind, DateTimeOffset timestamp)
    {
        return new CustomLoopRunEvent(sequence, eventId, timestamp, kind, null, null, null, kind.ToString(), [], null, null, null, null, null, null, null, null, null, null);
    }

    private static bool IsLifecycleControlEvent(CustomLoopRunEvent item)
    {
        return item.Kind is CustomLoopRunEventKind.LifecycleChanged or CustomLoopRunEventKind.IntegrityWarning;
    }

    private static async Task WriteDirectAsync(WorkspacePaths paths, CustomLoopRunRecord run)
    {
        using var canonicalWorkspace = new TestWorkspace();
        var canonicalPaths = new WorkspacePaths(canonicalWorkspace.RootPath);
        var created = await new CustomLoopRunStore(canonicalPaths).CreateAsync(run);
        Assert.Equal(CustomLoopRunStoreStatus.Created, created.Status);
        var source = Path.Combine(canonicalPaths.CustomLoopRunsPath, run.LoopId, run.Id + ".json");
        var content = await File.ReadAllBytesAsync(source);
        var directory = Path.Combine(paths.CustomLoopRunsPath, run.LoopId);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, run.Id + ".json"), content);
    }

    private static async Task<string> WriteRawAsync(WorkspacePaths paths, string loopId, string runId, string content)
    {
        var directory = Path.Combine(paths.CustomLoopRunsPath, loopId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, runId + ".json");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private static void AssertRun(CustomLoopRunRecord expected, CustomLoopRunRecord? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.LoopId, actual.LoopId);
        Assert.Equal(expected.LifecycleVersion, actual.LifecycleVersion);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.AdmissionOperationId, actual.AdmissionOperationId);
        Assert.Equal(expected.AdmittedDefinition.ContentHash, actual.AdmittedDefinition.ContentHash);
        Assert.Equal(expected.Events.Select(item => item.EventId), actual.Events.Select(item => item.EventId));
    }
}
