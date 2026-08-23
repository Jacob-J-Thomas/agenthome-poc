using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Diagnostics;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Tests.Authority.Grants;
using EmbodySense.Core.Common.Tests;
using EmbodySense.Core.Common.Tests.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopRunStoreTests
{
    private static readonly DateTimeOffset _timestamp = DateTimeOffset.Parse("2026-07-16T12:00:00+00:00");
    private static readonly JsonSerializerOptions _artifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    [Fact]
    public async Task Run_created_schedule_proof_remains_durable_while_provider_dispatch_can_still_resume()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var scheduled = Enumerable.Range(1, 4)
            .Select(ordinal => CreateScheduledRun(ordinal, ScheduleOverlapPolicy.Skip))
            .ToArray();

        var created = await store.CreateScheduledAsync(scheduled[0].Run, scheduled[0].Envelope);
        Assert.Equal(ScheduleRunAdmissionStoreStatus.Created, created.Status);
        foreach (var item in scheduled.Skip(1))
        {
            Assert.Equal(ScheduleRunAdmissionStoreStatus.OverlapSkipped, (await store.CreateScheduledAsync(item.Run, item.Envelope)).Status);
        }

        var retained = await store.GetScheduleAdmissionAsync(scheduled[0].Envelope.DeliveryId);
        Assert.NotNull(retained);
        Assert.Equal(ScheduleRunAdmissionDisposition.RunCreated, retained.Attempts[^1].Disposition);
        var replay = await new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath)).CreateScheduledAsync(scheduled[0].Run, scheduled[0].Envelope);
        Assert.Equal(ScheduleRunAdmissionStoreStatus.Replayed, replay.Status);
        Assert.Equal(created.Run!.Id, replay.Run!.Id);
        Assert.Equal(created.Run.SequentialAdapterBinding!.ContentHash, replay.Run.SequentialAdapterBinding!.ContentHash);
        Assert.NotNull(replay.Evidence);
    }

    [Fact]
    public async Task Terminal_schedule_admissions_compact_to_authenticated_watermarks_and_old_redelivery_cannot_redispatch()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(CreateRun("sequential-loop", "run-active", "invoke-active"))).Status);
        var scheduled = Enumerable.Range(1, 4)
            .Select(ordinal => CreateScheduledRun(ordinal, ScheduleOverlapPolicy.Skip))
            .ToArray();

        byte[]? interruptedDeletionContent = null;
        for (var index = 0; index < 3; index++)
        {
            var item = scheduled[index];
            Assert.Equal(ScheduleRunAdmissionStoreStatus.OverlapSkipped, (await store.CreateScheduledAsync(item.Run, item.Envelope)).Status);
            if (index == 0)
            {
                interruptedDeletionContent = await File.ReadAllBytesAsync(ordinalArtifact(item.Envelope.DeliveryId));
                Assert.NotEmpty(interruptedDeletionContent);
            }
        }

        Assert.Null(await store.GetScheduleAdmissionAsync(scheduled[0].Envelope.DeliveryId));
        Assert.NotNull(await store.GetScheduleAdmissionAsync(scheduled[1].Envelope.DeliveryId));
        Assert.NotNull(await store.GetScheduleAdmissionAsync(scheduled[2].Envelope.DeliveryId));
        var retirementPath = Path.Combine(paths.CustomLoopScheduleAdmissionsPath, ".schedule-admission-retirements.json");
        var retirement = JsonNode.Parse(await File.ReadAllTextAsync(retirementPath))!.AsObject();
        var retirementEntries = retirement["entries"]!.AsArray();
        Assert.Single(retirementEntries);
        Assert.Equal(1, retirementEntries[0]!["retiredThroughOccurrenceOrdinal"]!.GetValue<long>());
        Assert.Equal(64, retirement["contentHash"]!.GetValue<string>().Length);

        var interruptedArtifact = ordinalArtifact(scheduled[0].Envelope.DeliveryId);
        await File.WriteAllBytesAsync(interruptedArtifact, interruptedDeletionContent!);

        var restarted = new CustomLoopRunStore(paths);
        var retired = await restarted.CreateScheduledAsync(scheduled[0].Run, scheduled[0].Envelope);
        Assert.Equal(ScheduleRunAdmissionStoreStatus.Retired, retired.Status);
        Assert.Null(retired.Run);
        Assert.Null(retired.Evidence);
        Assert.False(File.Exists(interruptedArtifact));

        var substituted = CreateScheduledRun(1, ScheduleOverlapPolicy.Skip, "payload/substituted");
        var substitutedResult = await restarted.CreateScheduledAsync(substituted.Run, substituted.Envelope);
        Assert.Equal(ScheduleRunAdmissionStoreStatus.Conflict, substitutedResult.Status);
        Assert.Null(await restarted.GetScheduleAdmissionAsync(substituted.Envelope.DeliveryId));

        Assert.Equal(ScheduleRunAdmissionStoreStatus.OverlapSkipped, (await restarted.CreateScheduledAsync(scheduled[3].Run, scheduled[3].Envelope)).Status);
        Assert.Null(await restarted.GetScheduleAdmissionAsync(scheduled[1].Envelope.DeliveryId));
        Assert.NotNull(await restarted.GetScheduleAdmissionAsync(scheduled[2].Envelope.DeliveryId));
        Assert.NotNull(await restarted.GetScheduleAdmissionAsync(scheduled[3].Envelope.DeliveryId));
        Assert.Equal(2, Directory.EnumerateFiles(paths.CustomLoopScheduleAdmissionsPath, "*.json").Count(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal)));

        var canonicalRetirement = await File.ReadAllTextAsync(retirementPath);
        var corruptedRetirement = canonicalRetirement.Replace(
            JsonNode.Parse(canonicalRetirement)!["contentHash"]!.GetValue<string>(),
            new string('0', 64),
            StringComparison.Ordinal);
        Assert.NotEqual(canonicalRetirement, corruptedRetirement);
        await File.WriteAllTextAsync(retirementPath, corruptedRetirement);
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).CreateScheduledAsync(scheduled[0].Run, scheduled[0].Envelope));

        string ordinalArtifact(TriggerDeliveryId deliveryId)
            => Path.Combine(paths.CustomLoopScheduleAdmissionsPath, $"{deliveryId.Value}.json");
    }

    [Fact]
    public async Task Operational_pages_preserve_opaque_cursor_snapshots_reject_unknown_cursors_and_detach_results()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteDirectAsync(paths, At(CreateRun("loop-a", "run-a", "invoke-a"), 3));
        await WriteDirectAsync(paths, At(CreateRun("loop-b", "run-b", "invoke-b"), 2));
        await WriteDirectAsync(paths, At(CreateRun("loop-c", "run-c", "invoke-c"), 1));
        var adapter = new CustomLoopRunOperationalPostureAdapter(new CustomLoopRunStore(paths));

        var first = await adapter.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1));
        var firstAgain = await adapter.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1));
        Assert.Equal("run-a", Assert.Single(first.Items).Summary.Id);
        Assert.True(first.HasMore);
        Assert.NotNull(first.ContinuationCursor);
        Assert.NotSame(first.Items[0].Summary, firstAgain.Items[0].Summary);
        var exposed = Assert.IsAssignableFrom<IList<GovernedLoopRunEvidenceSnapshot>>(first.Items);
        Assert.Throws<NotSupportedException>(() => exposed.Add(first.Items[0]));

        await WriteDirectAsync(paths, At(CreateRun("loop-new", "run-new", "invoke-new"), 4));
        var second = await adapter.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, first.ContinuationCursor));
        var third = await adapter.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, second.ContinuationCursor));
        Assert.Equal("run-b", Assert.Single(second.Items).Summary.Id);
        Assert.Equal("run-c", Assert.Single(third.Items).Summary.Id);
        Assert.False(third.HasMore);
        Assert.Null(third.ContinuationCursor);

        var impossibleCursorJson = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            createdAtUtcTicks = DateTimeOffset.MinValue.UtcTicks,
            runId = "run-impossible-cursor",
            loopId = (string?)null
        });
        var impossibleCursor = Convert.ToBase64String(impossibleCursorJson).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(
            GovernedLoopOperationalEvidenceReadStatus.Corrupt,
            (await adapter.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, impossibleCursor))).Status);
        Assert.Equal(
            GovernedLoopOperationalEvidenceReadStatus.Corrupt,
            (await adapter.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, "bad cursor"))).Status);

        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopRunsPath, "loop-c", "run-c.json"), "{}");
        Assert.Equal(
            GovernedLoopOperationalEvidenceReadStatus.Corrupt,
            (await adapter.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1))).Status);
    }

    [Fact]
    public async Task Operational_pages_reconcile_a_valid_lifecycle_update_after_page_discovery()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var writer = new CustomLoopRunStore(paths);
        var admitted = CreateRun();
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await writer.CreateAsync(admitted)).Status);
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        var mutationCount = 0;
        using var reader = new CustomLoopRunStore(paths, path =>
        {
            Assert.Equal(1, Interlocked.Increment(ref mutationCount));
            var update = writer.UpdateAsync(running, admitted.LifecycleVersion).GetAwaiter().GetResult();
            Assert.Equal(CustomLoopRunStoreStatus.Updated, update.Status);
            return new FileSystemWatcher(path);
        });
        var adapter = new CustomLoopRunOperationalPostureAdapter(reader);

        var read = await adapter.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1));

        Assert.Equal(GovernedLoopOperationalEvidenceReadStatus.Found, read.Status);
        var snapshot = Assert.Single(read.Items);
        Assert.Equal(CustomLoopRunStatus.Running, snapshot.Summary.Status);
        Assert.Equal(running.LifecycleVersion, snapshot.Summary.LifecycleVersion);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(CustomLoopRunArtifactSerializer.Serialize(running))).ToLowerInvariant(),
            snapshot.EvidenceHash);
        Assert.Equal(1, mutationCount);
    }

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
    public async Task Pre_role_identity_trace_is_rejected_without_a_legacy_persistence_fallback()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        var legacyManifest = run.ContextSnapshot.SourceManifest.ToArray();
        legacyManifest[1] = legacyManifest[1] with { SourceId = "agent", SourcePath = "unavailable/.agent/AGENT.md" };
        legacyManifest[2] = legacyManifest[2] with
        {
            SourceType = CustomLoopContextSource.RoleInstruction,
            Provenance = CustomLoopContextProvenance.WorkspaceRoleFile
        };
        legacyManifest[3] = legacyManifest[3] with
        {
            SourceType = CustomLoopContextSource.RoleInstruction,
            Provenance = CustomLoopContextProvenance.WorkspaceRoleFile
        };
        var legacyContext = CustomLoopContextSnapshotHash.Apply(run.ContextSnapshot with { SourceManifest = legacyManifest });
        var unsupportedRun = CustomLoopAdmissionRequestHash.Apply(run with { ContextSnapshot = legacyContext, AdmissionRequestHash = string.Empty });

        var store = new CustomLoopRunStore(paths);

        await Assert.ThrowsAsync<FormatException>(() => store.CreateAsync(unsupportedRun));
        Assert.Empty(await store.ListRecentAsync(50));
        Assert.Equal(0, (await store.GetTraceQuotaAsync()).RetainedTraceCount);
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
    public async Task Update_rejects_a_noncanonical_prior_envelope_before_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun();
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var path = Path.Combine(paths.CustomLoopRunsPath, admitted.LoopId, admitted.Id + ".json");
        byte[] noncanonical = [.. await File.ReadAllBytesAsync(path), (byte)' '];
        await File.WriteAllBytesAsync(path, noncanonical);

        await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(Advance(admitted, CustomLoopRunStatus.Running), admitted.LifecycleVersion));

        Assert.Equal(noncanonical, await File.ReadAllBytesAsync(path));
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
        Assert.Null(await store.InspectTraceAsync("run-missing"));
        Assert.False(Directory.Exists(paths.CustomLoopRunsPath));
    }

    [Fact]
    public async Task Lock_free_reads_ignore_only_internal_temporary_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var run = CreateRun();
        await store.CreateAsync(run);
        var runDirectory = Path.Combine(paths.CustomLoopRunsPath, run.LoopId);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, $".{run.Id}.json.{Guid.NewGuid():N}.tmp"), "partial");

        AssertRun(run, await store.GetAsync(run.Id));
        AssertRun(run, await store.GetByAdmissionOperationAsync(run.AdmissionOperationId));
        Assert.Equal(run.Id, Assert.Single(await store.ListRecentAsync(50)).Id);
        Assert.Equal(run.Id, Assert.Single(await store.ListNonterminalAsync()).Id);

        await File.WriteAllTextAsync(Path.Combine(runDirectory, "unexpected.tmp"), "partial");
        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(run.Id));
    }

    [Fact]
    public async Task Mutation_lease_recovers_exact_orphaned_staging_artifacts_in_both_roots()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var runDirectory = Path.Combine(paths.CustomLoopRunsPath, "loop-alpha");
        Directory.CreateDirectory(runDirectory);
        Directory.CreateDirectory(paths.CustomLoopTraceDeletionOperationsPath);
        var runStagingPath = Path.Combine(runDirectory, $".run-alpha.json.{Guid.NewGuid():N}.tmp");
        var operationStagingPath = Path.Combine(paths.CustomLoopTraceDeletionOperationsPath, $".delete-trace.json.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(runStagingPath, "flushed partial trace");
        await File.WriteAllTextAsync(operationStagingPath, "flushed partial operation");

        var quota = await new CustomLoopRunStore(paths).GetTraceQuotaAsync();

        Assert.Equal(CustomLoopTraceQuota.Empty(), quota);
        Assert.False(File.Exists(runStagingPath));
        Assert.False(File.Exists(operationStagingPath));
    }

    [Fact]
    public async Task Mutation_lease_fails_closed_without_removing_unrecognized_temporary_looking_artifacts()
    {
        using var runWorkspace = new TestWorkspace();
        var runPaths = new WorkspacePaths(runWorkspace.RootPath);
        var runDirectory = Path.Combine(runPaths.CustomLoopRunsPath, "loop-alpha");
        Directory.CreateDirectory(runDirectory);
        var runArtifact = Path.Combine(runDirectory, ".run-alpha.json.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.tmp");
        await File.WriteAllTextAsync(runArtifact, "not an exact internal staging artifact");

        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(runPaths).GetTraceQuotaAsync());
        Assert.True(File.Exists(runArtifact));

        using var operationWorkspace = new TestWorkspace();
        var operationPaths = new WorkspacePaths(operationWorkspace.RootPath);
        Directory.CreateDirectory(operationPaths.CustomLoopTraceDeletionOperationsPath);
        var operationArtifact = Path.Combine(operationPaths.CustomLoopTraceDeletionOperationsPath, ".delete trace.json.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.tmp");
        await File.WriteAllTextAsync(operationArtifact, "not an exact internal staging artifact");

        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(operationPaths).GetTraceQuotaAsync());
        Assert.True(File.Exists(operationArtifact));
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
        var running = run with { Status = CustomLoopRunStatus.Running, ExecutionClock = new CustomLoopExecutionClock(0, _timestamp) };
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
    public async Task Windows_public_reader_releases_its_handle_before_a_paused_consumer_continuation()
    {
        // FileShare enforcement is Windows-specific; other platforms cannot exercise this contract.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun() with { TriggerPrompt = new string('p', CustomLoopLimits.MaxPresetPromptCharacters) };
        admitted = admitted with { Events = [admitted.Events[0] with { Detail = new string('x', CustomLoopLimits.MaxRunDetailCharacters) }] };
        admitted = CustomLoopAdmissionRequestHash.Apply(admitted with { AdmissionRequestHash = string.Empty });
        await store.CreateAsync(admitted);
        using var reader = new CustomLoopRunStore(paths);
        using var readCancellation = new CancellationTokenSource();
        var gated = new QueuedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task<CustomLoopRunRecord?>? readTask = null;
        Task<CustomLoopRunStoreResult>? updateTask = null;
        SynchronizationContext.SetSynchronizationContext(gated);
        try
        {
            readTask = reader.GetAsync(admitted.Id, readCancellation.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        try
        {
            await gated.WaitForPostAsync(TimeSpan.FromSeconds(10));
            Assert.False(readTask!.IsCompleted);
            var running = Advance(admitted, CustomLoopRunStatus.Running);
            updateTask = Task.Run(() => store.UpdateAsync(running, admitted.LifecycleVersion));
            var result = await updateTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(CustomLoopRunStoreStatus.Updated, result.Status);
            await gated.DrainUntilCompletedAsync(readTask!, TimeSpan.FromSeconds(10));
            Assert.Equal(admitted.Id, (await readTask)!.Id);
            Assert.Equal(CustomLoopRunStatus.Admitted, (await readTask)!.Status);
        }
        finally
        {
            if (readTask is not null && !readTask.IsCompleted)
            {
                readCancellation.Cancel();
            }

            if (readTask is not null)
            {
                await gated.DrainUntilCompletedAsync(readTask, TimeSpan.FromSeconds(10));
                try
                {
                    await readTask;
                }
                catch (OperationCanceledException) when (readCancellation.IsCancellationRequested)
                {
                }
            }

            if (updateTask is not null)
            {
                await updateTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task Windows_public_reader_preserves_old_snapshot_while_atomic_replace_publishes_new_snapshot()
    {
        // FileShare enforcement is Windows-specific; other platforms cannot exercise this contract.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        var readerOpened = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReader = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdFirstReader = 1;
        Func<CustomLoopRunReadBoundary, string, CancellationToken, ValueTask> observeReader = (boundary, _, _) =>
        {
            if (boundary == CustomLoopRunReadBoundary.AfterCanonicalArtifactReadOpen && Interlocked.Exchange(ref holdFirstReader, 0) == 1)
            {
                readerOpened.TrySetResult(null);
                return new ValueTask(releaseReader.Task);
            }

            return ValueTask.CompletedTask;
        };

        using var reader = new CustomLoopRunStore(paths, timeProvider: null, artifactReadObserver: observeReader);
        var admitted = CreateRun() with { TriggerPrompt = new string('p', CustomLoopLimits.MaxPresetPromptCharacters) };
        admitted = admitted with { Events = [admitted.Events[0] with { Detail = new string('x', CustomLoopLimits.MaxRunDetailCharacters) }] };
        admitted = CustomLoopAdmissionRequestHash.Apply(admitted with { AdmissionRequestHash = string.Empty });
        await store.CreateAsync(admitted);
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        var readTask = reader.GetAsync(admitted.Id);
        Task<CustomLoopRunStoreResult>? updateTask = null;
        try
        {
            await readerOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
            updateTask = store.UpdateAsync(running, admitted.LifecycleVersion);
            var result = await updateTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(CustomLoopRunStoreStatus.Updated, result.Status);
        }
        finally
        {
            releaseReader.TrySetResult(null);
            await readTask.WaitAsync(TimeSpan.FromSeconds(10));
            if (updateTask is not null)
            {
                await updateTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        var retained = await readTask;
        Assert.Equal(CustomLoopRunStatus.Admitted, retained?.Status);
        Assert.Equal(CustomLoopRunStatus.Running, (await store.GetAsync(admitted.Id))!.Status);
    }

    [Fact]
    public async Task ListRecent_reconciles_a_same_length_in_place_rewrite_after_its_first_snapshot()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var original = CustomLoopAdmissionRequestHash.Apply(CreateRun() with { TriggerPrompt = new string('a', 96), AdmissionRequestHash = string.Empty });
        var replacement = CustomLoopAdmissionRequestHash.Apply(original with { TriggerPrompt = new string('b', 96), AdmissionRequestHash = string.Empty });
        var originalContent = CustomLoopRunArtifactSerializer.Serialize(original);
        var replacementContent = CustomLoopRunArtifactSerializer.Serialize(replacement);
        Assert.Equal(originalContent.Length, replacementContent.Length);
        await WriteDirectAsync(paths, original);
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, original.LoopId, original.Id + ".json");
        var firstSnapshotCount = 0;
        var rewriteInjected = 0;
        Func<CustomLoopRunReadBoundary, string, CancellationToken, ValueTask> rewriteAfterFirstSnapshot = async (boundary, path, cancellationToken) =>
        {
            if (boundary == CustomLoopRunReadBoundary.AfterCanonicalArtifactReadFirstSnapshot && Interlocked.Increment(ref firstSnapshotCount) == 1)
            {
                Assert.Equal(artifactPath, path);
                Interlocked.Increment(ref rewriteInjected);
                await File.WriteAllBytesAsync(path, replacementContent, cancellationToken);
            }
        };

        using var store = new CustomLoopRunStore(paths, timeProvider: null, artifactReadObserver: rewriteAfterFirstSnapshot);
        var summaries = await store.ListRecentAsync(1);

        Assert.True(firstSnapshotCount >= 2);
        Assert.Equal(1, rewriteInjected);
        Assert.Equal(original.Id, Assert.Single(summaries).Id);
        var read = await store.GetAsync(original.Id);
        Assert.NotNull(read);
        Assert.Equal(replacement.TriggerPrompt, read.TriggerPrompt);
        Assert.Equal(replacement.AdmissionRequestHash, read.AdmissionRequestHash);
    }

    [Fact]
    public async Task Get_fails_closed_after_each_bounded_snapshot_attempt_observes_a_same_length_in_place_rewrite()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = CustomLoopAdmissionRequestHash.Apply(CreateRun() with { TriggerPrompt = new string('a', 96), AdmissionRequestHash = string.Empty });
        var second = CustomLoopAdmissionRequestHash.Apply(first with { TriggerPrompt = new string('b', 96), AdmissionRequestHash = string.Empty });
        var firstContent = CustomLoopRunArtifactSerializer.Serialize(first);
        var secondContent = CustomLoopRunArtifactSerializer.Serialize(second);
        Assert.Equal(firstContent.Length, secondContent.Length);
        await WriteDirectAsync(paths, first);
        var rewrites = 0;
        Func<CustomLoopRunReadBoundary, string, CancellationToken, ValueTask> rewriteEveryFirstSnapshot = async (boundary, path, cancellationToken) =>
        {
            if (boundary == CustomLoopRunReadBoundary.AfterCanonicalArtifactReadFirstSnapshot)
            {
                var replacement = Interlocked.Increment(ref rewrites) % 2 == 0 ? firstContent : secondContent;
                await File.WriteAllBytesAsync(path, replacement, cancellationToken);
            }
        };

        using var store = new CustomLoopRunStore(paths, timeProvider: null, artifactReadObserver: rewriteEveryFirstSnapshot);
        var exception = await Assert.ThrowsAnyAsync<IOException>(() => store.GetAsync(first.Id));

        Assert.Equal(3, rewrites);
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.Read, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
    }

    [Fact]
    public async Task Get_detects_an_in_place_truncation_after_its_first_snapshot_and_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        await WriteDirectAsync(paths, run);
        var firstSnapshotCallbacks = 0;
        var truncationInjected = 0;
        Func<CustomLoopRunReadBoundary, string, CancellationToken, ValueTask> truncateAfterFirstSnapshot = async (boundary, path, cancellationToken) =>
        {
            if (boundary == CustomLoopRunReadBoundary.AfterCanonicalArtifactReadFirstSnapshot)
            {
                Interlocked.Increment(ref firstSnapshotCallbacks);
                if (Interlocked.Exchange(ref truncationInjected, 1) == 0)
                {
                    await File.WriteAllBytesAsync(path, [(byte)'{'], cancellationToken);
                }
            }
        };

        using var store = new CustomLoopRunStore(paths, timeProvider: null, artifactReadObserver: truncateAfterFirstSnapshot);
        var exception = await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(run.Id));

        Assert.Equal(1, truncationInjected);
        Assert.Equal(2, firstSnapshotCallbacks);
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.Validate, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
    }

    [Fact]
    public async Task Get_retries_when_a_canonical_run_disappears_between_enumeration_and_open()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        await WriteDirectAsync(paths, run);
        var movedPath = workspace.File("temporarily-moved-run.json");
        var moved = 1;
        Func<CustomLoopRunReadBoundary, string, CancellationToken, ValueTask> observeRead = (boundary, path, _) =>
        {
            if (boundary == CustomLoopRunReadBoundary.BeforeCanonicalArtifactReadOpen && Interlocked.Exchange(ref moved, 0) == 1)
            {
                File.Move(path, movedPath);
            }
            else if (boundary == CustomLoopRunReadBoundary.AfterCanonicalArtifactReadMiss)
            {
                File.Move(movedPath, path);
            }

            return ValueTask.CompletedTask;
        };

        using var store = new CustomLoopRunStore(paths, timeProvider: null, artifactReadObserver: observeRead);
        AssertRun(run, await store.GetAsync(run.Id));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json")));
    }

    [Fact]
    public async Task Get_propagates_reader_observer_file_not_found_without_reconciling_it_as_a_filesystem_miss()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        await WriteDirectAsync(paths, run);
        var beforeOpenCalls = 0;
        Func<CustomLoopRunReadBoundary, string, CancellationToken, ValueTask> observeRead = (boundary, _, _) =>
        {
            if (boundary == CustomLoopRunReadBoundary.BeforeCanonicalArtifactReadOpen)
            {
                Interlocked.Increment(ref beforeOpenCalls);
                throw new FileNotFoundException("The observer intentionally rejected this read.");
            }

            return ValueTask.CompletedTask;
        };

        using var store = new CustomLoopRunStore(paths, timeProvider: null, artifactReadObserver: observeRead);
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => store.GetAsync(run.Id));

        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.Read, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
        Assert.Equal(1, beforeOpenCalls);
    }

    [Fact]
    public async Task Public_reads_classify_stream_access_as_read_and_content_validation_as_validate()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        await WriteDirectAsync(paths, run);
        Func<CustomLoopRunReadBoundary, string, CancellationToken, ValueTask> rejectRead = (boundary, _, _) =>
        {
            if (boundary == CustomLoopRunReadBoundary.BeforeCanonicalArtifactReadOpen)
            {
                throw new IOException("The stream could not be opened.");
            }

            return ValueTask.CompletedTask;
        };

        using (var rejectedStore = new CustomLoopRunStore(paths, timeProvider: null, artifactReadObserver: rejectRead))
        {
            var readException = await Assert.ThrowsAsync<IOException>(() => rejectedStore.GetAsync(run.Id));
            Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.Read, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(readException)).Stage);
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancellationException = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CustomLoopRunStore(paths).GetAsync(run.Id, cancellation.Token));
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.Read, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(cancellationException)).Stage);

        await WriteRawAsync(paths, run.LoopId, run.Id, "{invalid");
        var validationException = await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync(run.Id));
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.Validate, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(validationException)).Stage);
    }

    [Fact]
    public async Task Windows_atomic_replace_converges_after_a_short_external_reader_releases_the_destination()
    {
        // FileShare enforcement is Windows-specific; other platforms cannot exercise this contract.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun();
        await store.CreateAsync(admitted);
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, admitted.LoopId, admitted.Id + ".json");
        var artifactDirectory = Path.GetDirectoryName(artifactPath)!;
        var stagingPattern = $".{Path.GetFileName(artifactPath)}.*.tmp";
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        Task<CustomLoopRunStoreResult>? updateTask = null;
        CustomLoopRunStoreResult result;

        try
        {
            await using (var externalReader = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                updateTask = Task.Run(() => store.UpdateAsync(running, admitted.LifecycleVersion));
                var wait = Stopwatch.StartNew();
                while (!Directory.EnumerateFiles(artifactDirectory, stagingPattern).Any())
                {
                    Assert.False(updateTask.IsCompleted, "The atomic update exhausted its transient retry budget before the external reader released the destination.");
                    Assert.True(wait.Elapsed < TimeSpan.FromSeconds(10), "The atomic update did not reach its staged replacement boundary within the bounded wait.");
                    await Task.Delay(TimeSpan.FromMilliseconds(15));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250));
                Assert.False(updateTask.IsCompleted, "The atomic update exhausted its transient retry budget before the external reader released the destination.");
            }

            result = await updateTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (updateTask is not null && !updateTask.IsCompleted)
            {
                await updateTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else if (updateTask?.IsFaulted == true)
            {
                _ = updateTask.Exception;
            }
        }

        Assert.Equal(CustomLoopRunStoreStatus.Updated, result.Status);
        Assert.Equal(CustomLoopRunStatus.Running, (await store.GetAsync(admitted.Id))!.Status);
    }

    [Fact]
    public async Task Windows_atomic_replace_waits_past_the_legacy_budget_then_preserves_old_and_new_canonical_snapshots()
    {
        // FileShare enforcement is Windows-specific; other platforms cannot exercise this contract.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun();
        await store.CreateAsync(admitted);
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, admitted.LoopId, admitted.Id + ".json");
        var artifactDirectory = Path.GetDirectoryName(artifactPath)!;
        var stagingPattern = $".{Path.GetFileName(artifactPath)}.*.tmp";
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        Task<CustomLoopRunStoreResult>? updateTask = null;

        try
        {
            using (var externalReader = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var oldSnapshot = new byte[checked((int)externalReader.Length)];
                await externalReader.ReadExactlyAsync(oldSnapshot);
                Assert.Equal(CustomLoopRunStatus.Admitted, CustomLoopRunArtifactSerializer.Deserialize(oldSnapshot).Status);

                updateTask = store.UpdateAsync(running, admitted.LifecycleVersion);
                var wait = Stopwatch.StartNew();
                while (!Directory.EnumerateFiles(artifactDirectory, stagingPattern).Any())
                {
                    Assert.False(updateTask.IsCompleted, "The atomic update did not reach its staged replacement boundary within the bounded wait.");
                    Assert.True(wait.Elapsed < TimeSpan.FromSeconds(10), "The atomic update did not reach its staged replacement boundary within the bounded wait.");
                    await Task.Delay(TimeSpan.FromMilliseconds(15));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(2_250));
                Assert.False(updateTask.IsCompleted, "The atomic update did not retain its retry ownership beyond the legacy two-second window.");
            }

            var result = await updateTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(CustomLoopRunStoreStatus.Updated, result.Status);
        }
        finally
        {
            if (updateTask is not null && !updateTask.IsCompleted)
            {
                await updateTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else if (updateTask?.IsFaulted == true)
            {
                _ = updateTask.Exception;
            }
        }

        Assert.Equal(CustomLoopRunStatus.Running, (await store.GetAsync(admitted.Id))!.Status);
    }

    [Fact]
    public async Task Windows_atomic_replace_deadline_fails_closed_with_a_path_free_canonical_replace_diagnostic()
    {
        // FileShare enforcement is Windows-specific; other platforms cannot exercise this contract.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun();
        await store.CreateAsync(admitted);
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, admitted.LoopId, admitted.Id + ".json");
        var artifactDirectory = Path.GetDirectoryName(artifactPath)!;
        var stagingPattern = $".{Path.GetFileName(artifactPath)}.*.tmp";
        var oldSnapshot = await File.ReadAllBytesAsync(artifactPath);
        var running = Advance(admitted, CustomLoopRunStatus.Running);

        using var externalReader = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var updateTask = store.UpdateAsync(running, admitted.LifecycleVersion);
        var wait = Stopwatch.StartNew();
        while (!Directory.EnumerateFiles(artifactDirectory, stagingPattern).Any())
        {
            Assert.False(updateTask.IsCompleted, "The atomic update did not reach its staged replacement boundary within the bounded wait.");
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(10), "The atomic update did not reach its staged replacement boundary within the bounded wait.");
            await Task.Delay(TimeSpan.FromMilliseconds(15));
        }

        var replacementWindow = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAnyAsync<IOException>(async () => await updateTask.WaitAsync(TimeSpan.FromSeconds(9)));
        var diagnostic = Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception));

        Assert.InRange(replacementWindow.Elapsed, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8));
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalReplace, diagnostic.Stage);
        Assert.Equal(CustomLoopRunPersistenceNativeErrorKind.Win32, diagnostic.NativeErrorKind);
        Assert.True(diagnostic.NativeErrorCode is 5 or 32 or 33 or 1175);
        Assert.Equal(oldSnapshot, await File.ReadAllBytesAsync(artifactPath));
        Assert.Equal(CustomLoopRunStatus.Admitted, (await store.GetAsync(admitted.Id))!.Status);
    }

    [Fact]
    public async Task Windows_atomic_replace_caller_cancellation_before_the_next_retry_keeps_the_canonical_snapshot()
    {
        // FileShare enforcement is Windows-specific; other platforms cannot exercise this contract.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun();
        await store.CreateAsync(admitted);
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, admitted.LoopId, admitted.Id + ".json");
        var artifactDirectory = Path.GetDirectoryName(artifactPath)!;
        var stagingPattern = $".{Path.GetFileName(artifactPath)}.*.tmp";
        var oldSnapshot = await File.ReadAllBytesAsync(artifactPath);
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        using var cancellation = new CancellationTokenSource();
        Task<CustomLoopRunStoreResult>? updateTask = null;

        try
        {
            using (var externalReader = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                updateTask = store.UpdateAsync(running, admitted.LifecycleVersion, cancellation.Token);
                var wait = Stopwatch.StartNew();
                while (!Directory.EnumerateFiles(artifactDirectory, stagingPattern).Any())
                {
                    Assert.False(updateTask.IsCompleted, "The atomic update did not reach its staged replacement boundary within the bounded wait.");
                    Assert.True(wait.Elapsed < TimeSpan.FromSeconds(10), "The atomic update did not reach its staged replacement boundary within the bounded wait.");
                    await Task.Delay(TimeSpan.FromMilliseconds(15));
                }

                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await updateTask.WaitAsync(TimeSpan.FromSeconds(2)));
            }
        }
        finally
        {
            if (updateTask is not null && !updateTask.IsCompleted)
            {
                await updateTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else if (updateTask?.IsFaulted == true)
            {
                _ = updateTask.Exception;
            }
        }

        Assert.Equal(oldSnapshot, await File.ReadAllBytesAsync(artifactPath));
        Assert.Equal(CustomLoopRunStatus.Admitted, (await store.GetAsync(admitted.Id))!.Status);
    }

    [Fact]
    public async Task Windows_derived_index_replacement_retains_the_legacy_contention_budget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        await store.CreateAsync(CreateRun());
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var pendingPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending");

        // Permit discovery reads while withholding the sharing required to replace the derived index.
        using (var restrictiveReader = WindowsFileLock.OpenRestrictiveReader(indexPath, workspace.RootPath))
        {
            var replacementWindow = Stopwatch.StartNew();
            // This lower bound rules out an immediate bypass in the retained contention scenario; it does not claim
            // to isolate atomic-move time from the public CreateAsync work. The separate five-second outer guard
            // bounds that whole public operation with hosted scheduling margin and catches material budget regressions.
            var result = await store.CreateAsync(CreateRun("loop-derived-index", "run-derived-index", "invoke-derived-index")).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CustomLoopRunStoreStatus.Created, result.Status);
            Assert.True(replacementWindow.Elapsed >= TimeSpan.FromMilliseconds(1500), "The derived-index replacement completed without consuming the bounded contention budget.");
            Assert.True(File.Exists(pendingPath));
        }

        Assert.Equal(CustomLoopRunStatus.Admitted, (await store.GetAsync("run-derived-index"))!.Status);
        var repaired = await store.ListPageAsync(new CustomLoopRunPageRequest(50));
        Assert.Equal(2, repaired.Items.Count);
        Assert.Contains(repaired.Items, item => item.LoopId == "loop-alpha" && item.Id == "run-alpha");
        Assert.Contains(repaired.Items, item => item.LoopId == "loop-derived-index" && item.Id == "run-derived-index");
        Assert.False(File.Exists(pendingPath));
    }

    [Fact]
    public async Task Queued_synchronization_context_drains_a_posted_task_until_completion()
    {
        var gated = new QueuedSynchronizationContext();
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var post = Task.Run(() => gated.Post(_ => completion.TrySetResult(null), null));

        await gated.DrainUntilCompletedAsync(completion.Task, TimeSpan.FromSeconds(10));
        await post;

        Assert.True(completion.Task.IsCompletedSuccessfully);
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
    public async Task Cross_process_mutation_lease_preserves_active_staging_until_the_writer_exits()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var runDirectory = Path.Combine(paths.CustomLoopRunsPath, "loop-alpha");
        Directory.CreateDirectory(runDirectory);
        var lockPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-runs.lock");
        var stagingPath = Path.Combine(runDirectory, $".run-alpha.json.{Guid.NewGuid():N}.tmp");
        var readyPath = workspace.File("writer-ready");
        var releasePath = workspace.File("writer-release");
        using var writer = StartCrossProcessStagingWriter(lockPath, stagingPath, readyPath, releasePath);
        var outputTask = writer.StandardOutput.ReadToEndAsync();
        var errorTask = writer.StandardError.ReadToEndAsync();
        try
        {
            await WaitForFileAsync(readyPath, writer, TimeSpan.FromSeconds(30));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CustomLoopRunStore(paths).GetTraceQuotaAsync(cancellation.Token));
            Assert.True(File.Exists(stagingPath));

            await File.WriteAllTextAsync(releasePath, "release");
            await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(writer.ExitCode == 0, $"Cross-process staging writer failed with exit code {writer.ExitCode}.{Environment.NewLine}{await outputTask}{Environment.NewLine}{await errorTask}");

            Assert.Equal(CustomLoopTraceQuota.Empty(), await new CustomLoopRunStore(paths).GetTraceQuotaAsync());
            Assert.False(File.Exists(stagingPath));
        }
        finally
        {
            await File.WriteAllTextAsync(releasePath, "release");
            if (!writer.HasExited)
            {
                writer.Kill(entireProcessTree: true);
                await writer.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Unsafe_mutation_lock_fails_closed_instead_of_retrying_as_contention()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopRunsPath);
        var target = workspace.File("lock-target");
        await File.WriteAllTextAsync(target, "unsafe");
        var lockPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-runs.lock");
        try
        {
            File.CreateSymbolicLink(lockPath, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<IOException>(() => new CustomLoopRunStore(paths).CreateAsync(CreateRun(), cancellation.Token));
    }

    [Fact]
    public async Task Dangling_mutation_lock_symlink_is_rejected_without_creating_its_target()
    {
        using var workspace = new TestWorkspace();
        using var outsideWorkspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopRunsPath);
        var target = outsideWorkspace.File("missing-lock-target");
        var lockPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-runs.lock");
        try
        {
            File.CreateSymbolicLink(lockPath, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        await Assert.ThrowsAsync<IOException>(() => new CustomLoopRunStore(paths).CreateAsync(CreateRun()));
        Assert.False(File.Exists(target));
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
        var malformedReplay = warning with { ControlExpectedLifecycleVersion = completed.LifecycleVersion };
        Assert.Equal(CustomLoopRunStoreStatus.Conflict, (await store.AppendTerminalIntegrityWarningAsync(completed.Id, completed.LifecycleVersion, malformedReplay)).Status);
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
    public async Task Terminal_content_uses_its_permanent_reserve_outside_the_control_event_budget()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun();
        await store.CreateAsync(admitted);
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        await store.UpdateAsync(running, admitted.LifecycleVersion);
        var path = Path.Combine(paths.CustomLoopRunsPath, admitted.LoopId, admitted.Id + ".json");
        var runningBytes = new FileInfo(path).Length;
        var completed = Advance(running, CustomLoopRunStatus.Completed) with { FinalOutput = new string('x', CustomLoopLimits.MaxCanonicalModelOutputCharacters) };

        var result = await store.UpdateAsync(completed, running.LifecycleVersion);

        Assert.Equal(CustomLoopRunStoreStatus.Updated, result.Status);
        Assert.True(new FileInfo(path).Length - runningBytes > CustomLoopLimits.MaxTraceControlEventUtf8Bytes);
        Assert.Equal(completed.FinalOutput, (await new CustomLoopRunStore(paths).GetAsync(completed.Id))!.FinalOutput);
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
        var recent = CreateRun("loop-beta", "run-beta", "invoke-beta") with { CreatedAtUtc = _timestamp.AddMinutes(1), UpdatedAtUtc = _timestamp.AddMinutes(2) };
        recent = recent with { ContextSnapshot = CustomLoopContextSnapshot.CreateEmpty(_timestamp.AddMinutes(1)), Events = [recent.Events[0] with { TimestampUtc = _timestamp.AddMinutes(1) }] };
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
    public async Task Run_pages_use_stable_filter_bound_cursors_across_concurrent_inserts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteDirectAsync(paths, At(CreateRun("loop-alpha", "run-alpha-new", "invoke-alpha-new"), 5));
        await WriteDirectAsync(paths, At(CreateRun("loop-beta", "run-beta-new", "invoke-beta-new"), 4));
        await WriteDirectAsync(paths, At(CreateRun("loop-alpha", "run-alpha-middle", "invoke-alpha-middle"), 3));
        await WriteDirectAsync(paths, At(CreateRun("loop-beta", "run-beta-old", "invoke-beta-old"), 2));
        await WriteDirectAsync(paths, At(CreateRun("loop-alpha", "run-alpha-old", "invoke-alpha-old"), 1));
        var store = new CustomLoopRunStore(paths);

        var first = await store.ListPageAsync(new CustomLoopRunPageRequest(2));
        Assert.Equal(["run-alpha-new", "run-beta-new"], first.Items.Select(item => item.Id));
        Assert.NotNull(first.ContinuationCursor);

        await WriteDirectAsync(paths, At(CreateRun("loop-alpha", "run-concurrent-new", "invoke-concurrent-new"), 6));
        var second = await store.ListPageAsync(new CustomLoopRunPageRequest(2, Cursor: first.ContinuationCursor));
        var third = await store.ListPageAsync(new CustomLoopRunPageRequest(2, Cursor: second.ContinuationCursor));

        Assert.Equal(["run-alpha-middle", "run-beta-old"], second.Items.Select(item => item.Id));
        Assert.Equal(["run-alpha-old"], third.Items.Select(item => item.Id));
        Assert.DoesNotContain("run-concurrent-new", second.Items.Concat(third.Items).Select(item => item.Id));
        Assert.Null(third.ContinuationCursor);

        var filtered = await store.ListPageAsync(new CustomLoopRunPageRequest(2, "loop-alpha"));
        var filteredNext = await store.ListPageAsync(new CustomLoopRunPageRequest(2, "loop-alpha", filtered.ContinuationCursor));
        Assert.Equal(["run-concurrent-new", "run-alpha-new"], filtered.Items.Select(item => item.Id));
        Assert.Equal(["run-alpha-middle", "run-alpha-old"], filteredNext.Items.Select(item => item.Id));
        Assert.All(filtered.Items.Concat(filteredNext.Items), item => Assert.Equal("loop-alpha", item.LoopId));
        Assert.Null(filteredNext.ContinuationCursor);

        await Assert.ThrowsAsync<ArgumentException>(() => store.ListPageAsync(new CustomLoopRunPageRequest(2, Cursor: "not-a-cursor")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListPageAsync(new CustomLoopRunPageRequest(2, "loop-beta", filtered.ContinuationCursor)));
        var impossibleCursorJson = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, createdAtUtcTicks = DateTimeOffset.MinValue.UtcTicks, runId = "run-impossible-cursor", loopId = (string?)null });
        var impossibleCursor = Convert.ToBase64String(impossibleCursorJson).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListPageAsync(new CustomLoopRunPageRequest(2, Cursor: impossibleCursor)));
        var obsoleteCursorJson = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, createdAtUtcTicks = _timestamp.UtcTicks, runId = "run-obsolete-cursor", loopId = (string?)null });
        var obsoleteCursor = Convert.ToBase64String(obsoleteCursorJson).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListPageAsync(new CustomLoopRunPageRequest(2, Cursor: obsoleteCursor)));
    }

    [Fact]
    public async Task Run_page_index_keeps_unseen_updated_runs_in_immutable_cursor_order()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var newest = At(CreateRun("loop-alpha", "run-newest", "invoke-newest"), 3);
        var unseen = At(CreateRun("loop-beta", "run-unseen", "invoke-unseen"), 2);
        var oldest = At(CreateRun("loop-gamma", "run-oldest", "invoke-oldest"), 1);
        await WriteDirectAsync(paths, newest);
        await WriteDirectAsync(paths, unseen);
        await WriteDirectAsync(paths, oldest);
        var store = new CustomLoopRunStore(paths);

        var first = await store.ListPageAsync(new CustomLoopRunPageRequest(1));
        var updatedUnseen = Advance(unseen, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(updatedUnseen, unseen.LifecycleVersion)).Status);
        updatedUnseen = Advance(updatedUnseen, CustomLoopRunStatus.PauseRequested);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(updatedUnseen, updatedUnseen.LifecycleVersion - 1)).Status);
        var second = await store.ListPageAsync(new CustomLoopRunPageRequest(1, Cursor: first.ContinuationCursor));
        var third = await store.ListPageAsync(new CustomLoopRunPageRequest(1, Cursor: second.ContinuationCursor));

        Assert.Equal(newest.Id, Assert.Single(first.Items).Id);
        Assert.Equal(unseen.Id, Assert.Single(second.Items).Id);
        Assert.Equal(oldest.Id, Assert.Single(third.Items).Id);
        Assert.Null(third.ContinuationCursor);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        Assert.True(File.Exists(indexPath));
        Assert.Equal(1, JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!["schemaVersion"]!.GetValue<int>());
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
    }

    [Fact]
    public async Task Run_page_refuses_an_unsupported_discovery_index_schema_without_rewriting_it()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteDirectAsync(paths, At(CreateRun("loop-alpha", "run-alpha", "invoke-alpha"), 1));
        var store = new CustomLoopRunStore(paths);
        Assert.Equal("run-alpha", Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Id);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        index["schemaVersion"] = 2;
        index["unsupportedV2Field"] = "requires-cleanup";
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(_artifactJsonOptions) + "\n");

        var exception = await Assert.ThrowsAnyAsync<FormatException>(() => store.ListPageAsync(new CustomLoopRunPageRequest(50)));

        Assert.Contains("Delete `.custom-loop-run-index.json`", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task Pending_discovery_index_refuses_an_unsupported_schema_without_rewriting_it()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteDirectAsync(paths, At(CreateRun("loop-alpha", "run-alpha", "invoke-alpha"), 1));
        var store = new CustomLoopRunStore(paths);
        Assert.Equal("run-alpha", Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Id);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        index["schemaVersion"] = 2;
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(_artifactJsonOptions) + "\n");
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending"), "pending\n");

        await Assert.ThrowsAnyAsync<FormatException>(() => store.ListPageAsync(new CustomLoopRunPageRequest(50)));
        await Assert.ThrowsAnyAsync<FormatException>(() => store.CreateAsync(At(CreateRun("loop-beta", "run-beta", "invoke-beta"), 2)));

        Assert.Equal(2, JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!["schemaVersion"]!.GetValue<int>());
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, "loop-beta", "run-beta.json")));
    }

    [Fact]
    public async Task Run_page_index_rebuilds_after_a_pending_mutation_marker()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteDirectAsync(paths, At(CreateRun("loop-alpha", "run-alpha", "invoke-alpha"), 1));
        var store = new CustomLoopRunStore(paths);
        Assert.Equal("run-alpha", Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Id);
        var pendingPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending");
        var orphanedPendingTemporaryPath = Path.Combine(paths.CustomLoopRunsPath, $"..custom-loop-run-index.pending.{Guid.NewGuid():N}.tmp");
        var orphanedIndexTemporaryPath = Path.Combine(paths.CustomLoopRunsPath, $"..custom-loop-run-index.json.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(pendingPath, "pending\n");
        await File.WriteAllTextAsync(orphanedPendingTemporaryPath, "partial");
        await File.WriteAllTextAsync(orphanedIndexTemporaryPath, "partial");
        await WriteDirectAsync(paths, At(CreateRun("loop-beta", "run-beta", "invoke-beta"), 2));

        var repaired = await store.ListPageAsync(new CustomLoopRunPageRequest(50));

        Assert.Equal(["run-beta", "run-alpha"], repaired.Items.Select(item => item.Id));
        Assert.False(File.Exists(pendingPath));
        Assert.False(File.Exists(orphanedPendingTemporaryPath));
        Assert.False(File.Exists(orphanedIndexTemporaryPath));
    }

    [Fact]
    public async Task Run_page_index_rebuilds_when_its_summary_is_modified_without_its_canonical_binding()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = At(CreateRun("loop-alpha", "run-alpha", "invoke-alpha"), 1);
        await WriteDirectAsync(paths, run);
        var store = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStatus.Admitted, Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Status);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        index["entries"]![0]!["summary"]!["status"] = "failed";
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(_artifactJsonOptions) + "\n");

        var repaired = await store.ListPageAsync(new CustomLoopRunPageRequest(50));

        Assert.Equal(CustomLoopRunStatus.Admitted, Assert.Single(repaired.Items).Status);
        var repairedIndex = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        Assert.Equal("admitted", repairedIndex["entries"]![0]!["summary"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Run_page_index_rebuilds_when_a_modified_summary_recomputes_its_public_binding_hash()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = At(CreateRun("loop-alpha", "run-alpha", "invoke-alpha"), 1);
        await WriteDirectAsync(paths, run);
        var store = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStatus.Admitted, Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Status);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        var entry = index["entries"]![0]!.AsObject();
        entry["summary"]!["status"] = "failed";
        var modifiedSummary = entry["summary"]!.Deserialize<CustomLoopRunSummary>(_artifactJsonOptions)!;
        var artifactHash = entry["artifactHash"]!.GetValue<string>();
        using var bindingHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        bindingHash.AppendData(Convert.FromHexString(artifactHash));
        bindingHash.AppendData(JsonSerializer.SerializeToUtf8Bytes(modifiedSummary, _artifactJsonOptions));
        entry["summaryBindingHash"] = Convert.ToHexString(bindingHash.GetHashAndReset()).ToLowerInvariant();
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(_artifactJsonOptions) + "\n");

        var repaired = await store.ListPageAsync(new CustomLoopRunPageRequest(50));

        Assert.Equal(CustomLoopRunStatus.Admitted, Assert.Single(repaired.Items).Status);
        var repairedIndex = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        Assert.Equal("admitted", repairedIndex["entries"]![0]!["summary"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Run_page_index_rebuilds_invalid_derived_identifiers_for_reads_and_lifecycle_writes()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = At(CreateRun("loop-alpha", "run-alpha", "invoke-alpha"), 1);
        await WriteDirectAsync(paths, run);
        var store = new CustomLoopRunStore(paths);
        Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        index["entries"]![0]!["summary"]!["id"] = "../unsafe";
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(_artifactJsonOptions) + "\n");

        Assert.Equal(run.Id, Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Id);

        index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        index["entries"]![0]!["summary"]!["admissionOperationId"] = "../unsafe";
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(_artifactJsonOptions) + "\n");
        var updated = Advance(run, CustomLoopRunStatus.Running);

        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(updated, run.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStatus.Running, Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Status);
    }

    [Fact]
    public async Task Run_page_index_rebuilds_a_same_metadata_canonical_replacement()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = At(CreateRun("loop-alpha", "run-alpha", "invoke-alpha"), 1);
        await WriteDirectAsync(paths, run);
        var store = new CustomLoopRunStore(paths);
        Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var originalIndex = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        var originalHash = originalIndex["entries"]![0]!["artifactHash"]!.GetValue<string>();
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json");
        var originalInfo = new FileInfo(artifactPath);
        var originalLength = originalInfo.Length;
        var originalLastWriteUtc = originalInfo.LastWriteTimeUtc;
        using var replacementWorkspace = new TestWorkspace();
        var replacementPaths = new WorkspacePaths(replacementWorkspace.RootPath);
        var replacement = CustomLoopAdmissionRequestHash.Apply(run with { TriggerPrompt = "Altered prompt" });
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await new CustomLoopRunStore(replacementPaths).CreateAsync(replacement)).Status);
        var replacementPath = Path.Combine(replacementPaths.CustomLoopRunsPath, replacement.LoopId, replacement.Id + ".json");
        var replacementContent = await File.ReadAllBytesAsync(replacementPath);
        Assert.Equal(originalLength, replacementContent.Length);
        await File.WriteAllBytesAsync(artifactPath, replacementContent);
        File.SetLastWriteTimeUtc(artifactPath, originalLastWriteUtc);

        Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items);

        var repairedIndex = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        Assert.NotEqual(originalHash, repairedIndex["entries"]![0]!["artifactHash"]!.GetValue<string>());
    }

    [Fact]
    public async Task Run_page_rejects_unrelated_root_level_temporary_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-alpha", "run-alpha", "invoke-alpha");
        await WriteDirectAsync(paths, run);
        var store = new CustomLoopRunStore(paths);
        Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items);
        var unrelatedTemporaryPath = Path.Combine(paths.CustomLoopRunsPath, $".unrelated.json.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(unrelatedTemporaryPath, "unaccounted");

        await Assert.ThrowsAsync<FormatException>(() => store.ListPageAsync(new CustomLoopRunPageRequest(50)));

        Assert.True(File.Exists(unrelatedTemporaryPath));
    }

    [Fact]
    public async Task Run_page_uses_an_in_memory_rebuilt_index_when_derived_storage_is_read_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-alpha", "run-alpha", "invoke-alpha");
        await WriteDirectAsync(paths, run);
        var store = new CustomLoopRunStore(paths);
        Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var lockPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-runs.lock");
        File.Delete(indexPath);
        var originalDirectoryMode = File.GetUnixFileMode(paths.CustomLoopRunsPath);
        var originalLockMode = File.GetUnixFileMode(lockPath);
        try
        {
            File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.SetUnixFileMode(paths.CustomLoopRunsPath, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var page = await store.ListPageAsync(new CustomLoopRunPageRequest(50));

            Assert.Equal(run.Id, Assert.Single(page.Items).Id);
            Assert.False(File.Exists(indexPath));
        }
        finally
        {
            File.SetUnixFileMode(lockPath, originalLockMode);
            File.SetUnixFileMode(paths.CustomLoopRunsPath, originalDirectoryMode);
        }
    }

    [Fact]
    public async Task Repeated_exact_monitor_reads_use_the_unchanged_index_cache_without_rescanning_unrelated_storage()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-alpha", "run-alpha", "invoke-alpha");
        await WriteDirectAsync(paths, run);
        var store = new CustomLoopRunStore(paths);
        var first = await store.GetMonitorAsync(run.Id);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopRunsPath, ".unrelated-root-artifact"), "not part of the indexed run");

        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json");
        using var exclusiveArtifactLease = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var second = await store.GetMonitorAsync(run.Id);

        Assert.Equal(run.LifecycleVersion, first?.Summary.LifecycleVersion);
        Assert.Equal(run.LifecycleVersion, second?.Summary.LifecycleVersion);
        Assert.Equal(first?.ArtifactHash, second?.ArtifactHash);
        await Assert.ThrowsAsync<FormatException>(() => store.ListPageAsync(new CustomLoopRunPageRequest(50)));
    }

    [Fact]
    public async Task Exact_monitor_recreates_its_watcher_and_reestablishes_cache_certainty_after_an_error()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-alpha", "run-alpha", "invoke-alpha");
        await WriteDirectAsync(paths, run);
        var watchers = new List<ControllableFileSystemWatcher>();
        using var store = new CustomLoopRunStore(paths, path =>
        {
            var watcher = new ControllableFileSystemWatcher(path);
            watchers.Add(watcher);
            return watcher;
        });
        Assert.NotNull(await store.GetMonitorAsync(run.Id));
        var failedWatcher = Assert.Single(watchers);
        failedWatcher.RaiseError(new InternalBufferOverflowException("Simulated watcher overflow."));
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json");
        using (new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => store.GetMonitorAsync(run.Id));
        }

        var recovered = await store.GetMonitorAsync(run.Id);
        Assert.Equal(run.Id, recovered?.Summary.Id);
        Assert.Equal(2, watchers.Count);
        Assert.NotSame(failedWatcher, watchers[1]);
        using var exclusiveArtifactLease = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(run.Id, (await store.GetMonitorAsync(run.Id))?.Summary.Id);
    }

    [Fact]
    public async Task Exact_monitor_cache_rejects_an_unindexed_duplicate_run_id_in_another_loop()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-alpha", "run-alpha", "invoke-alpha");
        await WriteDirectAsync(paths, run);
        var store = new CustomLoopRunStore(paths);
        Assert.NotNull(await store.GetMonitorAsync(run.Id));
        await WriteDirectAsync(paths, CreateRun("loop-beta", run.Id, "invoke-beta"));
        var duplicatePath = Path.Combine(paths.CustomLoopRunsPath, "loop-beta", run.Id + ".json");
        var caseVariantDuplicatePath = Path.Combine(paths.CustomLoopRunsPath, "loop-beta", run.Id + ".JSON");
        File.Move(duplicatePath, caseVariantDuplicatePath);

        FormatException? failure = null;
        for (var attempt = 0; attempt < 20 && failure is null; attempt++)
        {
            await Task.Delay(25);
            try
            {
                await store.GetMonitorAsync(run.Id);
            }
            catch (FormatException exception)
            {
                failure = exception;
            }
        }

        Assert.NotNull(failure);
        Assert.Contains(run.Id, failure.Message, StringComparison.Ordinal);
        Assert.Contains("duplicat", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exact_monitor_cache_invalidates_when_a_created_artifact_changes_run_identity_topology()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-alpha", "run-alpha", "invoke-alpha");
        await WriteDirectAsync(paths, run);
        var watchers = new List<ControllableFileSystemWatcher>();
        using var store = new CustomLoopRunStore(paths, path =>
        {
            var watcher = new ControllableFileSystemWatcher(path);
            watchers.Add(watcher);
            return watcher;
        });
        Assert.NotNull(await store.GetMonitorAsync(run.Id));

        var duplicate = CreateRun("loop-beta", run.Id, "invoke-beta");
        await WriteDirectAsync(paths, duplicate);
        Assert.Single(watchers).RaiseCreated(Path.Combine(paths.CustomLoopRunsPath, duplicate.LoopId, duplicate.Id + ".json"));

        var exception = await Assert.ThrowsAsync<FormatException>(() => store.GetMonitorAsync(run.Id));
        Assert.Contains(run.Id, exception.Message, StringComparison.Ordinal);
        Assert.Contains("duplicat", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exact_monitor_rehydrates_a_persisted_discovery_index_before_taking_the_mutation_lease()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        await WriteDirectAsync(paths, run);
        using (var initial = new CustomLoopRunStore(paths))
        {
            Assert.Equal(run.Id, (await initial.GetMonitorAsync(run.Id))?.Summary.Id);
        }

        using var restarted = new CustomLoopRunStore(paths);
        var monitor = await restarted.GetMonitorAsync(run.Id);

        Assert.Equal(run.Id, monitor?.Summary.Id);
        Assert.Equal(run.LifecycleVersion, monitor?.Summary.LifecycleVersion);
    }

    [Fact]
    public async Task Exact_monitor_rebuild_returns_missing_after_a_deleted_artifact_topology_notification()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        await WriteDirectAsync(paths, run);
        var watchers = new List<ControllableFileSystemWatcher>();
        using var store = new CustomLoopRunStore(paths, path =>
        {
            var watcher = new ControllableFileSystemWatcher(path);
            watchers.Add(watcher);
            return watcher;
        });
        Assert.NotNull(await store.GetMonitorAsync(run.Id));
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json");
        File.Delete(artifactPath);
        Assert.Single(watchers).RaiseDeleted(artifactPath);

        Assert.Null(await store.GetMonitorAsync(run.Id));
        Assert.Empty((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items);
    }

    [Fact]
    public async Task Exact_monitor_cache_repairs_when_the_selected_canonical_artifact_disappears()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-alpha", "run-alpha", "invoke-alpha");
        await WriteDirectAsync(paths, run);
        var store = new CustomLoopRunStore(paths);
        Assert.NotNull(await store.GetMonitorAsync(run.Id));
        File.Delete(Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json"));

        var missing = await store.GetMonitorAsync(run.Id);

        Assert.Null(missing);
        Assert.Empty((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items);
    }

    [Fact]
    public async Task Exact_monitor_cache_repairs_a_same_metadata_canonical_replacement()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seed = CreateRun("loop-alpha", "run-alpha", "invoke-alpha");
        var store = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(seed)).Status);
        var running = Advance(seed, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, seed.LifecycleVersion)).Status);
        var first = await store.GetMonitorAsync(seed.Id);
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, seed.LoopId, seed.Id + ".json");
        var originalInfo = new FileInfo(artifactPath);
        var originalLength = originalInfo.Length;
        var originalLastWriteUtc = originalInfo.LastWriteTimeUtc;
        using var replacementWorkspace = new TestWorkspace();
        var replacementPaths = new WorkspacePaths(replacementWorkspace.RootPath);
        var replacementStore = new CustomLoopRunStore(replacementPaths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await replacementStore.CreateAsync(seed)).Status);
        var replacementTimestamp = running.UpdatedAtUtc.AddMinutes(1);
        var replacement = running with
        {
            UpdatedAtUtc = replacementTimestamp,
            ExecutionClock = running.ExecutionClock with { ActiveSinceUtc = replacementTimestamp },
            Events = [.. running.Events[..^1], running.Events[^1] with { TimestampUtc = replacementTimestamp }]
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await replacementStore.UpdateAsync(replacement, seed.LifecycleVersion)).Status);
        var replacementPath = Path.Combine(replacementPaths.CustomLoopRunsPath, seed.LoopId, seed.Id + ".json");
        var replacementContent = await File.ReadAllBytesAsync(replacementPath);
        Assert.Equal(originalLength, replacementContent.Length);
        await File.WriteAllBytesAsync(artifactPath, replacementContent);
        File.SetLastWriteTimeUtc(artifactPath, originalLastWriteUtc);

        CustomLoopRunMonitor? repaired = null;
        for (var attempt = 0; attempt < 20 && repaired?.Summary.UpdatedAtUtc != replacementTimestamp; attempt++)
        {
            await Task.Delay(25);
            repaired = await store.GetMonitorAsync(seed.Id);
        }

        Assert.Equal(running.UpdatedAtUtc, first?.Summary.UpdatedAtUtc);
        Assert.Equal(replacementTimestamp, repaired?.Summary.UpdatedAtUtc);
    }

    [Fact]
    public async Task Exact_monitor_cache_hashes_a_same_metadata_replacement_before_watcher_delivery()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seed = CreateRun("loop-alpha", "run-alpha", "invoke-alpha");
        var watchers = new List<ControllableFileSystemWatcher>();
        using var store = new CustomLoopRunStore(paths, path =>
        {
            var watcher = new ControllableFileSystemWatcher(path);
            watchers.Add(watcher);
            return watcher;
        });
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(seed)).Status);
        var running = Advance(seed, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, seed.LifecycleVersion)).Status);
        var first = await store.GetMonitorAsync(seed.Id);
        Assert.Equal(running.UpdatedAtUtc, first?.Summary.UpdatedAtUtc);
        Assert.Single(watchers).EnableRaisingEvents = false;

        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, seed.LoopId, seed.Id + ".json");
        var originalInfo = new FileInfo(artifactPath);
        var originalLength = originalInfo.Length;
        var originalLastWriteUtc = originalInfo.LastWriteTimeUtc;
        using var replacementWorkspace = new TestWorkspace();
        var replacementPaths = new WorkspacePaths(replacementWorkspace.RootPath);
        var replacementStore = new CustomLoopRunStore(replacementPaths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await replacementStore.CreateAsync(seed)).Status);
        var replacementTimestamp = running.UpdatedAtUtc.AddMinutes(1);
        var replacement = running with
        {
            UpdatedAtUtc = replacementTimestamp,
            ExecutionClock = running.ExecutionClock with { ActiveSinceUtc = replacementTimestamp },
            Events = [.. running.Events[..^1], running.Events[^1] with { TimestampUtc = replacementTimestamp }]
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await replacementStore.UpdateAsync(replacement, seed.LifecycleVersion)).Status);
        var replacementPath = Path.Combine(replacementPaths.CustomLoopRunsPath, seed.LoopId, seed.Id + ".json");
        var replacementContent = await File.ReadAllBytesAsync(replacementPath);
        Assert.Equal(originalLength, replacementContent.Length);
        await File.WriteAllBytesAsync(artifactPath, replacementContent);
        File.SetLastWriteTimeUtc(artifactPath, originalLastWriteUtc);

        var repaired = await store.GetMonitorAsync(seed.Id);

        Assert.Equal(replacementTimestamp, repaired?.Summary.UpdatedAtUtc);
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
            var root = JsonNode.Parse(JsonSerializer.Serialize(run, _artifactJsonOptions))!.AsObject();
            mutate(root);
            await WriteRawAsync(paths, run.LoopId, run.Id, root.ToJsonString(_artifactJsonOptions));

            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync(run.Id));
        }
    }

    [Fact]
    public async Task Strict_reader_rejects_duplicate_properties_and_invalid_json()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        var json = JsonSerializer.Serialize(run, _artifactJsonOptions);
        var schemaProperty = $"\"schemaVersion\": {CustomLoopRunRecord.CurrentSchemaVersion}";
        var duplicate = json.Replace(schemaProperty, schemaProperty + ",\n  " + schemaProperty, StringComparison.Ordinal);
        await WriteRawAsync(paths, run.LoopId, run.Id, duplicate);
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync(run.Id));

        await WriteRawAsync(paths, run.LoopId, run.Id, "{invalid");
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
        await WriteRawAsync(paths, "loop-other", "run-alpha", JsonSerializer.Serialize(CreateRun(), _artifactJsonOptions));
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
        await WriteDirectBatchAsync(
            paths,
            Enumerable.Range(0, CustomLoopLimits.MaxRunTracesPerWorkspace)
                .Select(index => CreateRun($"loop-{index:D3}", $"run-{index:D3}", $"invoke-{index:D3}")));

        var extra = CreateRun("loop-extra", "run-extra", "invoke-extra");
        var store = new CustomLoopRunStore(paths);
        var monitor = await store.GetMonitorAsync("run-249");
        var result = await store.CreateAsync(extra);

        Assert.Equal("run-249", monitor?.Summary.Id);
        Assert.Equal(CustomLoopRunStatus.Admitted, monitor?.Summary.Status);
        Assert.Equal(CustomLoopRunStoreStatus.LimitExceeded, result.Status);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, extra.LoopId, extra.Id + ".json")));
        Assert.Equal(CustomLoopLimits.MaxRunTracesPerWorkspace, Directory.EnumerateFiles(paths.CustomLoopRunsPath, "*.json", SearchOption.AllDirectories).Count(path => !string.Equals(Path.GetDirectoryName(path), paths.CustomLoopRunsPath, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Create_enforces_the_restart_derived_one_gibibyte_reservation_without_allocating_sparse_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var maximumReservations = checked((int)(CustomLoopLimits.MaxRunTraceWorkspaceUtf8Bytes / CustomLoopLimits.MaxRunTraceUtf8Bytes));
        await WriteDirectBatchAsync(
            paths,
            Enumerable.Range(0, maximumReservations)
                .Select(index => CreateRun($"loop-{index:D3}", $"run-{index:D3}", $"invoke-{index:D3}")));

        var store = new CustomLoopRunStore(paths);
        var quota = await store.GetTraceQuotaAsync();
        var recent = await store.ListRecentAsync(1);
        var nonterminal = await store.ListNonterminalAsync();
        var extra = CreateRun("loop-extra", "run-extra", "invoke-extra");
        var result = await store.CreateAsync(extra);

        Assert.Equal(maximumReservations, quota.ActiveReservationCount);
        Assert.Equal(CustomLoopLimits.MaxRunTraceWorkspaceUtf8Bytes, quota.AccountedTraceUtf8Bytes);
        Assert.Equal(0, quota.AvailableAccountedUtf8Bytes);
        Assert.Equal("run-000", Assert.Single(recent).Id);
        Assert.Equal(maximumReservations, nonterminal.Count);
        Assert.Equal(CustomLoopRunStoreStatus.LimitExceeded, result.Status);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, extra.LoopId, extra.Id + ".json")));
        Assert.Equal(maximumReservations, Directory.EnumerateFiles(paths.CustomLoopRunsPath, "*.json", SearchOption.AllDirectories).Count(path => !string.Equals(Path.GetDirectoryName(path), paths.CustomLoopRunsPath, StringComparison.Ordinal)));
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
    public async Task Create_rejects_a_valid_artifact_that_cannot_retain_terminal_capacity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        var blocks = Enumerable.Range(0, 48).Select(index =>
        {
            var prefix = index.ToString("D2") + ":";
            var content = prefix + new string('x', CustomLoopLimits.MaxLogicalProviderRequestCharacters - prefix.Length);
            return new CustomLoopContextBlock(CustomLoopContextSource.HarnessGovernance, $"source-{index}", LlmMessageRole.System, true, null, content, CustomLoopTraceContentHash.Compute(content), content.Length, false, EmbodySenseDeveloperInstructions.CurrentVersion);
        }).ToArray();
        run = run with { Events = [run.Events[0] with { ContextBlocks = blocks }] };
        var serialized = CustomLoopRunArtifactSerializer.Serialize(run);

        Assert.True(serialized.LongLength <= CustomLoopLimits.MaxRunTraceUtf8Bytes);

        var result = await new CustomLoopRunStore(paths).CreateAsync(run);

        Assert.Equal(CustomLoopRunStoreStatus.LimitExceeded, result.Status);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json")));
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
        var definition = CustomLoopDefinition.CreateSeed(loopId, "default-role", "step-1", "create-loop", _timestamp);
        var context = CustomLoopContextSnapshot.CreateEmpty(_timestamp);
        var admitted = Event(1, "event-1", CustomLoopRunEventKind.Admitted, _timestamp);
        var run = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, runId, loopId, 1, CustomLoopRunStatus.Admitted, _timestamp, _timestamp, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), operationId, "test-user", string.Empty, definition, "Initial prompt", null, context, CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [admitted], null, null, null) { CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, _timestamp) };
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static (CustomLoopRunRecord Run, EmbodySense.Core.Common.Triggers.Models.TriggerDeliveryEnvelope Envelope) CreateScheduledRun(
        long ordinal,
        ScheduleOverlapPolicy overlap,
        string payloadReference = "payload/daily-reflection")
    {
        const string Prompt = "Execute the exact admitted request.";
        var graph = CustomLoopSequentialEvidenceStoreTests.LinearGraph(scheduleTrigger: true);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, graph.RevisionReference, "publish-sequential", new string('7', 64));
        var grant = AuthorityGrantTestFixture.Grant();
        var grantReference = new EmbodySense.Core.Common.Authority.Grants.Models.AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
        Assert.True(EmbodySense.Core.Common.Triggers.TriggerDeliveryFactory.TryCreateGovernedLoopReference(publication, grantReference, out var target, out _));
        var actor = AuthorityGrantTestFixture.Actor("user-owner");
        var workspaceId = new string('1', 64);
        Assert.True(EmbodySense.Core.Common.Triggers.TriggerDeliveryFactory.TryCreateActorContext(actor, "web", workspaceId, graph.OwningRole.Identity.RoleId, out var actorContext, out _));
        var payload = TriggerDeliveryTestData.InlinePayload(System.Text.Encoding.UTF8.GetBytes(Prompt));
        var firstAtUtc = _timestamp.AddMinutes(-10);
        var timeZone = ScheduleContractTestData.TimeZone("Etc/UTC", new string('f', 64));
        var definition = ScheduleContractTestData.Definition() with
        {
            Overlap = overlap,
            Target = target!,
            ActorId = actor,
            SurfaceId = "web",
            WorkspaceId = workspaceId,
            RoleId = graph.OwningRole.Identity.RoleId,
            Payload = new SchedulePayloadReference(payloadReference, payload.ContentHash),
            Recurrence = new ScheduleRecurrenceRule(ScheduleRecurrenceKind.FixedInterval, DateTime.SpecifyKind(firstAtUtc.UtcDateTime, DateTimeKind.Unspecified), 1),
            TimeZone = timeZone,
        };
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out var definitionValidation), ScheduleContractTestData.Errors(definitionValidation));
        var scheduledAtUtc = firstAtUtc.AddSeconds(ordinal - 1);
        var occurrence = ScheduleContractTestData.Occurrence(
            ordinal,
            DateTime.SpecifyKind(scheduledAtUtc.UtcDateTime, DateTimeKind.Unspecified),
            scheduledAtUtc,
            timeZone);
        var prepared = ScheduleContractTestData.Prepared(
            occurrence,
            definitionHash: definitionHash!,
            definitionRevision: definition.Revision,
            scheduleId: definition.ScheduleId,
            target: definition.Target,
            adapter: definition.TimeAdapter,
            actorContext: actorContext,
            payload: payload,
            overlap: overlap);
        var identity = ScheduleContractTestData.Identity(occurrence, definitionHash!, definition.Revision, definition.ScheduleId);
        var provenance = new ScheduleDeliveryProvenanceEvidence(
            ScheduleDeliveryProvenanceEvidence.CurrentSchemaVersion,
            definition,
            definitionHash!,
            occurrence,
            identity,
            ScheduleContractTestData.Result(prepared.CanonicalEnvelopeHash, ScheduleDeliveryResultKind.Queued, prepared.PreparedAtUtc.AddSeconds(1)));
        Assert.True(GovernedLoopSequentialTriggerOriginFactory.TryCreateSchedule(prepared.Envelope, provenance, out var origin));
        var context = CustomLoopSequentialEvidenceStoreTests.CreateContext(origin, $"schedule-{ordinal}", scheduleTrigger: true);
        return (context.Run, prepared.Envelope);
    }

    private static CustomLoopRunRecord At(CustomLoopRunRecord run, int minutes)
    {
        var timestamp = _timestamp.AddMinutes(minutes);
        run = run with
        {
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp,
            ContextSnapshot = CustomLoopContextSnapshot.CreateEmpty(timestamp),
            Events = [run.Events[0] with { TimestampUtc = timestamp }]
        };
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

    [Fact]
    public async Task Public_reads_fail_closed_for_corrupt_run_artifact_layouts()
    {
        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            Directory.CreateDirectory(paths.CustomLoopRunsPath);
            await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopRunsPath, "unexpected-root-artifact"), "evidence");
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync("run-alpha"));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            Directory.CreateDirectory(Path.Combine(paths.CustomLoopRunsPath, "unsafe loop"));
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync("run-alpha"));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            Directory.CreateDirectory(Path.Combine(paths.CustomLoopRunsPath, "loop-alpha", "nested"));
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync("run-alpha"));
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetTraceQuotaAsync());
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            await WriteRawAsync(paths, "loop-alpha", "run-alpha", "{");
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync("run-alpha"));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            await WriteRawAsync(paths, "loop-alpha", "run-alpha", "{\"artifactKind\":5}");
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync("run-alpha"));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            await WriteRawAsync(paths, "loop-alpha", "run-alpha", "{\"artifactKind\":\"unsupported\"}");
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync("run-alpha"));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var run = CreateRun("loop-alpha", "run-alpha", "invoke-alpha");
            await WriteDirectAsync(paths, run);
            File.Move(Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json"), Path.Combine(paths.CustomLoopRunsPath, run.LoopId, "renamed.json"));
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync("renamed"));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            await WriteDirectAsync(paths, CreateRun("loop-alpha", "run-alpha", "invoke-alpha"));
            await WriteDirectAsync(paths, CreateRun("loop-beta", "run-alpha", "invoke-beta"));
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync("run-alpha"));
        }
    }

    [Fact]
    public async Task Public_index_and_operation_reads_reject_corrupt_derived_evidence()
    {
        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var store = new CustomLoopRunStore(paths);
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(CreateRun())).Status);
            var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
            await File.WriteAllTextAsync(indexPath, "{");
            var repaired = await new CustomLoopRunStore(paths).ListPageAsync(new CustomLoopRunPageRequest(50));
            Assert.Equal("run-alpha", Assert.Single(repaired.Items).Id);
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            Directory.CreateDirectory(paths.CustomLoopTraceDeletionOperationsPath);
            await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopTraceDeletionOperationsPath, "delete-trace.json"), "{");
            var request = new CustomLoopTraceDeletionRequest("run-alpha", new string('a', 64), "delete-trace", "actor-user", "web");
            var mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), _timestamp);
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).ReserveTraceDeletionOperationAsync(mutation));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            Directory.CreateDirectory(Path.Combine(paths.CustomLoopTraceDeletionOperationsPath, "nested"));
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetTraceQuotaAsync());
        }
    }

    [Fact]
    public async Task Monitor_remains_conservative_after_invalid_watcher_paths_and_rechecks_changed_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-alpha", "run-alpha", "invoke-alpha");
        await WriteDirectAsync(paths, run);
        var watchers = new List<ControllableFileSystemWatcher>();
        using var store = new CustomLoopRunStore(paths, path =>
        {
            var watcher = new ControllableFileSystemWatcher(path);
            watchers.Add(watcher);
            return watcher;
        });

        Assert.Equal(run.Id, (await store.GetMonitorAsync(run.Id))?.Summary.Id);
        var watcher = Assert.Single(watchers);
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json");
        watcher.RaiseChanged(artifactPath);
        Assert.Equal(run.Id, (await store.GetMonitorAsync(run.Id))?.Summary.Id);
        watcher.RaiseChanged("\0");
        watcher.RaiseCreated("\0");

        Assert.Equal(run.Id, (await store.GetMonitorAsync(run.Id))?.Summary.Id);
        Assert.Null(await store.GetMonitorAsync("run-missing"));
    }

    [Fact]
    public async Task Public_index_reads_repair_invalid_revision_and_entry_metadata()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteDirectAsync(paths, CreateRun("loop-alpha", "run-alpha", "invoke-alpha"));
        var store = new CustomLoopRunStore(paths);
        Assert.Equal("run-alpha", Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Id);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");

        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        index["revision"] = 0;
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(_artifactJsonOptions) + "\n");
        Assert.Equal("run-alpha", Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Id);

        index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        index["entries"]![0]!["artifactUtf8Bytes"] = 0;
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(_artifactJsonOptions) + "\n");
        Assert.Equal("run-alpha", Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Id);

        using var orderingWorkspace = new TestWorkspace();
        var orderingPaths = new WorkspacePaths(orderingWorkspace.RootPath);
        await WriteDirectAsync(orderingPaths, CreateRun("loop-alpha", "run-alpha", "invoke-alpha"));
        await WriteDirectAsync(orderingPaths, At(CreateRun("loop-beta", "run-beta", "invoke-beta"), 1));
        var orderingStore = new CustomLoopRunStore(orderingPaths);
        Assert.Equal(2, (await orderingStore.ListPageAsync(new CustomLoopRunPageRequest(50))).Items.Count);
        var orderingIndexPath = Path.Combine(orderingPaths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var orderingIndex = JsonNode.Parse(await File.ReadAllTextAsync(orderingIndexPath))!.AsObject();
        var entries = orderingIndex["entries"]!.AsArray();
        var first = entries[0]!.DeepClone();
        entries[0] = entries[1]!.DeepClone();
        entries[1] = first;
        await File.WriteAllTextAsync(orderingIndexPath, orderingIndex.ToJsonString(_artifactJsonOptions) + "\n");
        Assert.Equal(2, (await orderingStore.ListPageAsync(new CustomLoopRunPageRequest(50))).Items.Count);
    }

    [Fact]
    public async Task Deleted_traces_reject_create_update_and_terminal_warning_mutations()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun("loop-alpha", "run-alpha", "invoke-alpha");
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, admitted.LifecycleVersion)).Status);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(completed, running.LifecycleVersion)).Status);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(completed.Id));
        var request = new CustomLoopTraceDeletionRequest(completed.Id, inspection.PersistedArtifactHash, "delete-trace", "actor-user", "web");
        var mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), _timestamp.AddMinutes(3));
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(mutation)).Status);

        Assert.Equal(CustomLoopRunStoreStatus.DeletedIdentityConflict, (await store.CreateAsync(admitted)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.DeletedIdentityConflict, (await store.UpdateAsync(completed, running.LifecycleVersion)).Status);
        var warning = Event(completed.Events.Length + 1L, "event-integrity-warning", CustomLoopRunEventKind.IntegrityWarning, completed.UpdatedAtUtc.AddMinutes(1));
        Assert.Equal(CustomLoopRunStoreStatus.DeletedIdentityConflict, (await store.AppendTerminalIntegrityWarningAsync(completed.Id, completed.LifecycleVersion, warning)).Status);

        var tracePath = Path.Combine(paths.CustomLoopRunsPath, completed.LoopId, completed.Id + ".json");
        var renamedPath = Path.Combine(paths.CustomLoopRunsPath, completed.LoopId, "renamed.json");
        File.Move(tracePath, renamedPath);
        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync("renamed"));
        File.Move(renamedPath, tracePath);
        await File.AppendAllTextAsync(tracePath, new string(' ', CustomLoopLimits.MaxRunTraceTombstoneUtf8Bytes));
        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(completed.Id));
    }

    [Fact]
    public async Task Public_operation_reads_reject_filename_mismatch_empty_content_and_invalid_request_hashes()
    {
        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var storedRequest = new CustomLoopTraceDeletionRequest("run-alpha", new string('a', 64), "stored-operation", "actor-user", "web");
            var storedMutation = new CustomLoopTraceDeletionMutation(storedRequest, CustomLoopTraceDeletionRequestHash.Compute(storedRequest), _timestamp);
            var operation = new CustomLoopTraceDeletionOperation(CustomLoopTraceDeletionOperation.CurrentSchemaVersion, storedRequest.OperationId, storedMutation.RequestHash, storedRequest, _timestamp, _timestamp, CustomLoopTraceDeletionOperationState.PendingMutation, CustomLoopTraceDeletionStoreStatus.Unknown, null, CustomLoopTraceDeletionIntegrity.Unknown);
            Directory.CreateDirectory(paths.CustomLoopTraceDeletionOperationsPath);
            await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopTraceDeletionOperationsPath, "requested-operation.json"), JsonSerializer.Serialize(operation, _artifactJsonOptions) + "\n");
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetTraceDeletionOperationAsync("requested-operation"));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            Directory.CreateDirectory(paths.CustomLoopTraceDeletionOperationsPath);
            await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopTraceDeletionOperationsPath, "empty-operation.json"), string.Empty);
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetTraceDeletionOperationAsync("empty-operation"));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var request = new CustomLoopTraceDeletionRequest("run-alpha", new string('A', 64), "delete-trace", "actor-user", "web");
            var mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), _timestamp);
            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).ReserveTraceDeletionOperationAsync(mutation));
        }
    }

    [Fact]
    public async Task Public_index_reads_repair_contract_shape_corruption()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteDirectAsync(paths, CreateRun("loop-alpha", "run-alpha", "invoke-alpha"));
        var store = new CustomLoopRunStore(paths);
        Assert.Equal("run-alpha", Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Id);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");

        await File.WriteAllTextAsync(indexPath, "[]");
        Assert.Equal("run-alpha", Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Id);

        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        index["entries"] = new JsonObject();
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(_artifactJsonOptions) + "\n");
        Assert.Equal("run-alpha", Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Id);

        index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        index.Remove("revision");
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(_artifactJsonOptions) + "\n");
        Assert.Equal("run-alpha", Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Id);
    }

    private static async Task WriteDirectAsync(WorkspacePaths paths, CustomLoopRunRecord run)
    {
        var content = CustomLoopRunArtifactSerializer.Serialize(run);
        var directory = Path.Combine(paths.CustomLoopRunsPath, run.LoopId);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, run.Id + ".json"), content);
    }

    private static async Task WriteDirectBatchAsync(WorkspacePaths paths, IEnumerable<CustomLoopRunRecord> runs)
    {
        await Parallel.ForEachAsync(
            runs,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) },
            async (run, _) => await WriteDirectAsync(paths, run));
    }

    private static async Task<string> WriteRawAsync(WorkspacePaths paths, string loopId, string runId, string content)
    {
        var directory = Path.Combine(paths.CustomLoopRunsPath, loopId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, runId + ".json");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private static Process StartCrossProcessStagingWriter(string lockPath, string stagingPath, string readyPath, string releasePath)
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = outputDirectory.Name;
        var configuration = outputDirectory.Parent?.Name ?? throw new DirectoryNotFoundException("The active test build configuration could not be resolved.");
        var hostAssembly = Path.Combine(FindRepositoryRoot(), "tests", "EmbodySense.CancellationHost", "bin", configuration, targetFramework, "EmbodySense.CancellationHost.dll");
        Assert.True(File.Exists(hostAssembly), $"Cancellation host assembly was not built at `{hostAssembly}`.");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(hostAssembly);
        startInfo.ArgumentList.Add("custom-loop-run-stage");
        startInfo.ArgumentList.Add(lockPath);
        startInfo.ArgumentList.Add(stagingPath);
        startInfo.ArgumentList.Add(readyPath);
        startInfo.ArgumentList.Add(releasePath);
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The cross-process staging writer did not start.");
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

    private static async Task WaitForFileAsync(string path, Process process, TimeSpan timeout)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The cross-process staging writer exited before signaling readiness with exit code {process.ExitCode}.");
            }

            if (wait.Elapsed >= timeout)
            {
                throw new TimeoutException("The cross-process staging writer did not signal readiness within the bounded wait.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(15));
        }
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

    private sealed class ControllableFileSystemWatcher : FileSystemWatcher
    {
        public ControllableFileSystemWatcher(string path) : base(path)
        {
        }

        public void RaiseError(Exception exception)
        {
            OnError(new ErrorEventArgs(exception));
        }

        public void RaiseCreated(string path)
        {
            OnCreated(new FileSystemEventArgs(WatcherChangeTypes.Created, System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path)));
        }

        public void RaiseChanged(string path)
        {
            OnChanged(new FileSystemEventArgs(WatcherChangeTypes.Changed, string.Empty, path));
        }

        public void RaiseDeleted(string path)
        {
            OnDeleted(new FileSystemEventArgs(WatcherChangeTypes.Deleted, System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path)));
        }
    }
}
