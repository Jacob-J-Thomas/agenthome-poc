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
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopRunStoreTests
{
    private const string CrossProcessLockPathVariable = "EMBODYSENSE_TEST_CUSTOM_LOOP_LOCK_PATH";
    private const string CrossProcessReadyPathVariable = "EMBODYSENSE_TEST_CUSTOM_LOOP_READY_PATH";
    private const string CrossProcessReleasePathVariable = "EMBODYSENSE_TEST_CUSTOM_LOOP_RELEASE_PATH";
    private const string CrossProcessStagingPathVariable = "EMBODYSENSE_TEST_CUSTOM_LOOP_STAGING_PATH";
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
            await WaitForFileAsync(readyPath, writer, TimeSpan.FromSeconds(15));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CustomLoopRunStore(paths).GetTraceQuotaAsync(cancellation.Token));
            Assert.True(File.Exists(stagingPath));

            await File.WriteAllTextAsync(releasePath, "release");
            await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
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
    public async Task Cross_process_staging_writer_holds_mutation_lease_for_recovery_test()
    {
        var lockPath = Environment.GetEnvironmentVariable(CrossProcessLockPathVariable);
        if (string.IsNullOrWhiteSpace(lockPath))
        {
            return;
        }

        var stagingPath = Environment.GetEnvironmentVariable(CrossProcessStagingPathVariable) ?? throw new InvalidOperationException("The cross-process staging path is required.");
        var readyPath = Environment.GetEnvironmentVariable(CrossProcessReadyPathVariable) ?? throw new InvalidOperationException("The cross-process ready path is required.");
        var releasePath = Environment.GetEnvironmentVariable(CrossProcessReleasePathVariable) ?? throw new InvalidOperationException("The cross-process release path is required.");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
        await using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        await File.WriteAllTextAsync(stagingPath, "active staging content");
        await File.WriteAllTextAsync(readyPath, "ready");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(releasePath))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(15), cancellation.Token);
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
        var obsoleteCursorJson = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, createdAtUtcTicks = Timestamp.UtcTicks, runId = "run-obsolete-cursor", loopId = (string?)null });
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
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(ArtifactJsonOptions) + "\n");

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
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(ArtifactJsonOptions) + "\n");
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
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(ArtifactJsonOptions) + "\n");

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
        var modifiedSummary = entry["summary"]!.Deserialize<CustomLoopRunSummary>(ArtifactJsonOptions)!;
        var artifactHash = entry["artifactHash"]!.GetValue<string>();
        using var bindingHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        bindingHash.AppendData(Convert.FromHexString(artifactHash));
        bindingHash.AppendData(JsonSerializer.SerializeToUtf8Bytes(modifiedSummary, ArtifactJsonOptions));
        entry["summaryBindingHash"] = Convert.ToHexString(bindingHash.GetHashAndReset()).ToLowerInvariant();
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(ArtifactJsonOptions) + "\n");

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
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(ArtifactJsonOptions) + "\n");

        Assert.Equal(run.Id, Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Id);

        index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        index["entries"]![0]!["summary"]!["admissionOperationId"] = "../unsafe";
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(ArtifactJsonOptions) + "\n");
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
    public async Task Strict_reader_rejects_duplicate_properties_and_invalid_json()
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
        for (var index = 0; index < maximumReservations; index++)
        {
            await WriteDirectAsync(paths, CreateRun($"loop-{index:D3}", $"run-{index:D3}", $"invoke-{index:D3}"));
        }

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
        var definition = CustomLoopDefinition.CreateSeed(loopId, "default-role", "step-1", "create-loop", Timestamp);
        var context = CustomLoopContextSnapshot.CreateEmpty(Timestamp);
        var admitted = Event(1, "event-1", CustomLoopRunEventKind.Admitted, Timestamp);
        var run = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, runId, loopId, 1, CustomLoopRunStatus.Admitted, Timestamp, Timestamp, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), operationId, "test-user", string.Empty, definition, "Initial prompt", null, context, CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [admitted], null, null, null);
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static CustomLoopRunRecord At(CustomLoopRunRecord run, int minutes)
    {
        var timestamp = Timestamp.AddMinutes(minutes);
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

    private static Process StartCrossProcessStagingWriter(string lockPath, string stagingPath, string readyPath, string releasePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(typeof(CustomLoopRunStoreTests).Assembly.Location);
        startInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRunStoreTests.Cross_process_staging_writer_holds_mutation_lease_for_recovery_test");
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[CrossProcessLockPathVariable] = lockPath;
        startInfo.Environment[CrossProcessStagingPathVariable] = stagingPath;
        startInfo.Environment[CrossProcessReadyPathVariable] = readyPath;
        startInfo.Environment[CrossProcessReleasePathVariable] = releasePath;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The cross-process staging writer did not start.");
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
    }
}
