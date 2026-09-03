using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Diagnostics;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
using EmbodySense.Core.Persistence.Tests.Verification;
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
    public async Task Canonical_graph_frontier_trace_capacity_uses_the_graph_hard_ceiling_not_the_legacy_projection_ceiling()
    {
        var legacyRun = CreateRun();
        var legacy = legacyRun with { Events = [.. legacyRun.Events, .. CreateClosedProviderAttempts("legacy", legacyRun.CreatedAtUtc)] };
        using var legacyWorkspace = new TestWorkspace();
        var legacyStore = new CustomLoopRunStore(new WorkspacePaths(legacyWorkspace.RootPath));

        await Assert.ThrowsAsync<FormatException>(() => legacyStore.CreateAsync(legacy));

        var canonicalContext = CustomLoopSequentialEvidenceStoreTests.CreateContext();
        var canonical = canonicalContext.Run with
        {
            Events = [.. canonicalContext.Run.Events, .. CreateClosedProviderAttempts("canonical", canonicalContext.Run.CreatedAtUtc)]
        };
        using var canonicalWorkspace = new TestWorkspace();
        var canonicalStore = new CustomLoopRunStore(new WorkspacePaths(canonicalWorkspace.RootPath));

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await canonicalStore.CreateAsync(canonical)).Status);

        static CustomLoopRunEvent[] CreateClosedProviderAttempts(string prefix, DateTimeOffset timestampUtc)
        {
            return Enumerable.Range(1, 3)
                .SelectMany(attempt =>
                {
                    var startSequence = attempt * 2L;
                    return new[]
                    {
                        Event(startSequence, $"{prefix}-provider-start-{attempt}", CustomLoopRunEventKind.NodeAttemptStarted, timestampUtc) with
                        {
                            Iteration = 1,
                            StepId = "step-1",
                            Attempt = attempt,
                            TraceReservationUtf8Bytes = CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes,
                        },
                        Event(startSequence + 1, $"{prefix}-provider-completed-{attempt}", CustomLoopRunEventKind.NodeAttemptCompleted, timestampUtc) with
                        {
                            Iteration = 1,
                            StepId = "step-1",
                            Attempt = attempt,
                        },
                    };
                })
                .ToArray();
        }
    }

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
    public async Task Scheduled_admission_recovers_a_canonical_run_without_derived_evidence_and_replays_after_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var scheduled = CreateScheduledRun(1, ScheduleOverlapPolicy.Skip);
        await WriteDirectAsync(paths, scheduled.Run);

        var recovered = await new CustomLoopRunStore(paths).CreateScheduledAsync(scheduled.Run, scheduled.Envelope);

        Assert.Equal(ScheduleRunAdmissionStoreStatus.Replayed, recovered.Status);
        AssertRun(scheduled.Run, recovered.Run);
        Assert.NotNull(recovered.Evidence);
        Assert.Equal(ScheduleRunAdmissionDisposition.RunCreated, recovered.Evidence.Attempts[^1].Disposition);
        AssertRun(scheduled.Run, await new CustomLoopRunStore(paths).GetAsync(scheduled.Run.Id));
        var replay = await new CustomLoopRunStore(paths).CreateScheduledAsync(scheduled.Run, scheduled.Envelope);
        Assert.Equal(ScheduleRunAdmissionStoreStatus.Replayed, replay.Status);
        AssertRun(scheduled.Run, replay.Run);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(paths.CustomLoopRunsPath, scheduled.Run.LoopId), "*.json"));
    }

    [Fact]
    public async Task Scheduled_admission_fails_closed_when_multiple_canonical_runs_claim_one_delivery()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var scheduled = CreateScheduledRun(1, ScheduleOverlapPolicy.Skip);
        var duplicate = CustomLoopSequentialEvidenceStoreTests.CreateContext(scheduled.Run.SequentialInvocationSnapshot!.TriggerOrigin, "schedule-duplicate", scheduleTrigger: true).Run;
        await WriteDirectAsync(paths, scheduled.Run);
        await WriteDirectAsync(paths, duplicate);

        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).CreateScheduledAsync(scheduled.Run, scheduled.Envelope));
        Assert.Null(await new CustomLoopRunStore(paths).GetScheduleAdmissionAsync(scheduled.Envelope.DeliveryId));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(paths.CustomLoopRunsPath, scheduled.Run.LoopId), "*.json").Count());
    }

    [Fact]
    public async Task Scheduled_admission_rejects_a_conflicting_definition_before_creating_another_canonical_run()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = CreateScheduledRun(1, ScheduleOverlapPolicy.Skip);
        var conflicting = CreateScheduledRun(2, ScheduleOverlapPolicy.Skip, "payload/conflicting-definition");
        using var store = new CustomLoopRunStore(paths);

        Assert.Equal(ScheduleRunAdmissionStoreStatus.Created, (await store.CreateScheduledAsync(admitted.Run, admitted.Envelope)).Status);
        var rejected = await store.CreateScheduledAsync(conflicting.Run, conflicting.Envelope);

        Assert.Equal(ScheduleRunAdmissionStoreStatus.Conflict, rejected.Status);
        Assert.Null(rejected.Run);
        Assert.NotNull(rejected.Evidence);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(paths.CustomLoopRunsPath, admitted.Run.LoopId), "*.json"));
    }

    [Fact]
    public async Task Scheduled_admission_fails_closed_when_run_created_evidence_loses_its_canonical_artifact()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var scheduled = CreateScheduledRun(1, ScheduleOverlapPolicy.Skip);
        using var store = new CustomLoopRunStore(paths);
        Assert.Equal(ScheduleRunAdmissionStoreStatus.Created, (await store.CreateScheduledAsync(scheduled.Run, scheduled.Envelope)).Status);
        File.Delete(Path.Combine(paths.CustomLoopRunsPath, scheduled.Run.LoopId, scheduled.Run.Id + ".json"));

        await Assert.ThrowsAsync<FormatException>(() => store.CreateScheduledAsync(scheduled.Run, scheduled.Envelope));
        Assert.NotNull(await store.GetScheduleAdmissionAsync(scheduled.Envelope.DeliveryId));
    }

    [Fact]
    public async Task Scheduled_redelivery_of_a_tombstoned_run_conflicts_when_derived_admission_evidence_is_missing()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var scheduled = CreateScheduledRun(1, ScheduleOverlapPolicy.Skip);
        using var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun(scheduled.Run.LoopId, scheduled.Run.Id, scheduled.Run.AdmissionOperationId);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, admitted.LifecycleVersion)).Status);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(completed, running.LifecycleVersion)).Status);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(completed.Id));
        var request = new CustomLoopTraceDeletionRequest(completed.Id, inspection.PersistedArtifactHash, "delete-scheduled-redelivery", "actor-user", "web");
        var mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), _timestamp.AddMinutes(8));
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(mutation)).Status);

        var redelivery = await store.CreateScheduledAsync(scheduled.Run, scheduled.Envelope);

        Assert.Equal(ScheduleRunAdmissionStoreStatus.Conflict, redelivery.Status);
        Assert.Null(redelivery.Run);
        Assert.Null(redelivery.Evidence);
        var reusedRunId = CreateRun("replacement-loop", completed.Id, "replacement-operation");
        Assert.Equal(CustomLoopRunStoreStatus.DeletedIdentityConflict, (await store.CreateAsync(reusedRunId)).Status);
        Assert.NotNull(Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(completed.Id)).Tombstone);
    }

    [Fact]
    public async Task Scheduled_admission_conflicts_with_an_existing_non_schedule_operation_identity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var scheduled = CreateScheduledRun(1, ScheduleOverlapPolicy.Skip);
        var collision = CreateRun("unrelated-loop", "run-operation-collision", scheduled.Run.AdmissionOperationId);
        using var store = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(collision)).Status);

        var result = await store.CreateScheduledAsync(scheduled.Run, scheduled.Envelope);

        Assert.Equal(ScheduleRunAdmissionStoreStatus.Conflict, result.Status);
        Assert.Equal(collision.Id, result.Run!.Id);
        Assert.Null(result.Evidence);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, scheduled.Run.LoopId, scheduled.Run.Id + ".json")));
    }

    [Fact]
    public async Task Trace_deletion_reservation_fails_before_canonical_mutation_when_its_operation_directory_is_occupied_by_a_file()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun("loop-deletion-directory", "run-deletion-directory", "invoke-deletion-directory");
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, admitted.LifecycleVersion)).Status);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(completed, running.LifecycleVersion)).Status);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(completed.Id));
        Directory.CreateDirectory(Path.GetDirectoryName(paths.CustomLoopTraceDeletionOperationsPath)!);
        await File.WriteAllTextAsync(paths.CustomLoopTraceDeletionOperationsPath, "occupied");
        var request = new CustomLoopTraceDeletionRequest(completed.Id, inspection.PersistedArtifactHash, "delete-occupied-directory", "actor-user", "web");
        var mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), _timestamp.AddMinutes(9));

        await Assert.ThrowsAsync<IOException>(() => store.ReserveTraceDeletionOperationAsync(mutation));

        Assert.Equal(CustomLoopTraceArtifactKind.LiveTrace, Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(completed.Id)).Kind);
        Assert.True(File.Exists(paths.CustomLoopTraceDeletionOperationsPath));
    }

    [Fact]
    public async Task Scheduled_admission_rejects_invalid_or_substituted_delivery_evidence_before_canonical_publication()
    {
        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var scheduled = CreateScheduledRun(1, ScheduleOverlapPolicy.Skip);
            var invalidLifecycle = CustomLoopAdmissionRequestHash.Apply(scheduled.Run with { LifecycleVersion = 2 });

            await Assert.ThrowsAsync<ArgumentException>(() => new CustomLoopRunStore(paths).CreateScheduledAsync(invalidLifecycle, scheduled.Envelope));
            Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, scheduled.Run.LoopId, scheduled.Run.Id + ".json")));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var scheduled = CreateScheduledRun(1, ScheduleOverlapPolicy.Skip);
            var substitutedEnvelope = CreateScheduledRun(2, ScheduleOverlapPolicy.Skip).Envelope;

            await Assert.ThrowsAsync<ArgumentException>(() => new CustomLoopRunStore(paths).CreateScheduledAsync(scheduled.Run, substitutedEnvelope));
            Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, scheduled.Run.LoopId, scheduled.Run.Id + ".json")));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var original = CreateScheduledRun(1, ScheduleOverlapPolicy.Skip);
            var substituted = CreateScheduledRun(1, ScheduleOverlapPolicy.Skip, "payload/substituted-delivery");
            using var store = new CustomLoopRunStore(paths);
            Assert.Equal(ScheduleRunAdmissionStoreStatus.Created, (await store.CreateScheduledAsync(original.Run, original.Envelope)).Status);

            var conflict = await store.CreateScheduledAsync(substituted.Run, substituted.Envelope);

            Assert.Equal(ScheduleRunAdmissionStoreStatus.Conflict, conflict.Status);
            Assert.NotNull(conflict.Evidence);
            Assert.Single(Directory.EnumerateFiles(Path.Combine(paths.CustomLoopRunsPath, original.Run.LoopId), "*.json"));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var scheduled = CreateScheduledRun(1, ScheduleOverlapPolicy.Skip);
            Directory.CreateDirectory(paths.CustomLoopScheduleAdmissionsPath);
            await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopScheduleAdmissionsPath, "unsafe-evidence.json"), "{}\n");

            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).CreateScheduledAsync(scheduled.Run, scheduled.Envelope));
            Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, scheduled.Run.LoopId, scheduled.Run.Id + ".json")));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var original = CreateScheduledRun(1, ScheduleOverlapPolicy.Skip);
            var substituted = CreateScheduledRun(2, ScheduleOverlapPolicy.Skip);
            using var store = new CustomLoopRunStore(paths);
            Assert.Equal(ScheduleRunAdmissionStoreStatus.Created, (await store.CreateScheduledAsync(original.Run, original.Envelope)).Status);
            var originalPath = Path.Combine(paths.CustomLoopScheduleAdmissionsPath, original.Envelope.DeliveryId.Value + ".json");
            var substitutedPath = Path.Combine(paths.CustomLoopScheduleAdmissionsPath, substituted.Envelope.DeliveryId.Value + ".json");
            File.Move(originalPath, substitutedPath);

            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).CreateScheduledAsync(substituted.Run, substituted.Envelope));
            Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, substituted.Run.LoopId, substituted.Run.Id + ".json")));
        }
    }

    [Fact]
    public async Task Scheduled_admission_returns_a_limit_when_authenticated_evidence_reaches_its_attempt_bound()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var scheduled = CreateScheduledRun(1, ScheduleOverlapPolicy.Allow);
        Assert.True(TriggerDeliveryJson.TrySerialize(scheduled.Envelope, out var canonicalEnvelope, out _));
        Assert.True(TriggerDeliveryHash.TryCompute(scheduled.Envelope, out var canonicalEnvelopeHash, out _));
        var attempts = Enumerable.Range(1, ScheduleRunAdmissionEvidenceLimits.MaxAttempts)
            .Select(ordinal => new ScheduleRunAdmissionAttempt(
                ScheduleRunAdmissionAttempt.CurrentSchemaVersion,
                ordinal,
                ScheduleRunAdmissionDisposition.OverlapSerialized,
                scheduled.Run.AdmissionOperationId,
                scheduled.Run.Id,
                "run-blocker",
                _timestamp.AddSeconds(ordinal)))
            .ToArray();
        var evidence = ScheduleRunAdmissionEvidenceHash.Apply(new ScheduleRunAdmissionEvidence(
            ScheduleRunAdmissionEvidence.CurrentSchemaVersion,
            canonicalEnvelope!,
            canonicalEnvelopeHash!,
            scheduled.Run.LoopId,
            attempts,
            string.Empty));
        Assert.True(ScheduleRunAdmissionEvidenceValidator.IsValid(evidence));
        Directory.CreateDirectory(paths.CustomLoopScheduleAdmissionsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopScheduleAdmissionsPath, scheduled.Envelope.DeliveryId.Value + ".json"), JsonSerializer.Serialize(evidence, _artifactJsonOptions) + "\n");

        var result = await new CustomLoopRunStore(paths).CreateScheduledAsync(scheduled.Run, scheduled.Envelope);

        Assert.Equal(ScheduleRunAdmissionStoreStatus.LimitExceeded, result.Status);
        Assert.Null(result.Run);
        Assert.Equal(ScheduleRunAdmissionEvidenceLimits.MaxAttempts, result.Evidence!.Attempts.Count);
    }

    [Fact]
    public async Task Pending_schedule_admission_page_rejects_an_out_of_range_candidate_limit_before_reading_storage()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new CustomLoopRunStore(paths).ListPendingScheduleAdmissionsAsync(null, 0));

        Assert.False(Directory.Exists(paths.CustomLoopScheduleAdmissionsPath));
    }

    [Fact]
    public async Task Empty_pending_schedule_admission_page_does_not_create_custom_run_or_schedule_storage()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);

        Assert.Empty(await store.ListPendingScheduleAdmissionsAsync(null, 10));

        Assert.False(Directory.Exists(paths.CustomLoopRunsPath));
        Assert.False(Directory.Exists(paths.CustomLoopScheduleAdmissionsPath));
    }

    [Fact]
    public async Task Empty_existing_schedule_admission_root_does_not_create_custom_run_storage()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopScheduleAdmissionsPath);
        using var store = new CustomLoopRunStore(paths);

        Assert.Empty(await store.ListPendingScheduleAdmissionsAsync(null, 10));

        Assert.False(Directory.Exists(paths.CustomLoopRunsPath));
        Assert.True(Directory.Exists(paths.CustomLoopScheduleAdmissionsPath));
    }

    [Fact]
    public async Task Scheduled_defer_one_admissions_retain_one_deferred_occurrence_and_replay_each_outcome()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(CreateRun("sequential-loop", "run-active", "invoke-active"))).Status);
        var first = CreateScheduledRun(1, ScheduleOverlapPolicy.DeferOne);
        var second = CreateScheduledRun(2, ScheduleOverlapPolicy.DeferOne);

        Assert.Equal(ScheduleRunAdmissionStoreStatus.OverlapDeferred, (await store.CreateScheduledAsync(first.Run, first.Envelope)).Status);
        Assert.Equal(ScheduleRunAdmissionStoreStatus.DeferredOneSuppressed, (await store.CreateScheduledAsync(second.Run, second.Envelope)).Status);
        Assert.Equal(ScheduleRunAdmissionStoreStatus.OverlapDeferred, (await store.CreateScheduledAsync(first.Run, first.Envelope)).Status);
        Assert.Equal(ScheduleRunAdmissionStoreStatus.DeferredOneSuppressed, (await store.CreateScheduledAsync(second.Run, second.Envelope)).Status);
        Assert.Single(await store.ListPendingScheduleAdmissionsAsync(null, 10));
    }

    [Fact]
    public async Task Scheduled_allow_admission_serializes_against_an_active_canonical_run_and_replays_its_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(CreateRun("sequential-loop", "run-active", "invoke-active"))).Status);
        var scheduled = CreateScheduledRun(1, ScheduleOverlapPolicy.Allow);

        Assert.Equal(ScheduleRunAdmissionStoreStatus.OverlapSerialized, (await store.CreateScheduledAsync(scheduled.Run, scheduled.Envelope)).Status);
        Assert.Equal(ScheduleRunAdmissionStoreStatus.OverlapSerialized, (await store.CreateScheduledAsync(scheduled.Run, scheduled.Envelope)).Status);
        var evidence = Assert.IsType<ScheduleRunAdmissionEvidence>(await store.GetScheduleAdmissionAsync(scheduled.Envelope.DeliveryId));
        Assert.Equal(ScheduleRunAdmissionDisposition.OverlapSerialized, evidence.Attempts[^1].Disposition);
        Assert.Equal("run-active", evidence.Attempts[^1].BlockingRunId);
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
        Assert.Contains("\"humanInputWaitingCheckpoints\":[]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("isTerminal", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.EndsWith("\n", json, StringComparison.Ordinal);

        var restarted = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        AssertRun(run, await restarted.GetAsync(run.Id));
        Assert.Empty((await restarted.GetAsync(run.Id))!.HumanInputWaitingCheckpoints);
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
    public async Task Windows_get_retries_through_short_sharing_contention_and_returns_a_coherent_atomic_update_snapshot()
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
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, admitted.LoopId, admitted.Id + ".json");
        Task<CustomLoopRunStoreResult> updateTask;
        Task<CustomLoopRunRecord?> readTask;

        await using (var externalWriter = new FileStream(artifactPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            readTask = store.GetAsync(admitted.Id);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            Assert.False(readTask.IsCompleted);
        }

        updateTask = store.UpdateAsync(running, admitted.LifecycleVersion);

        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await updateTask.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        var observed = Assert.IsType<CustomLoopRunRecord>(await readTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(admitted.Id, observed.Id);
        Assert.Equal(admitted.LoopId, observed.LoopId);
        Assert.True(observed.Status is CustomLoopRunStatus.Admitted or CustomLoopRunStatus.Running);
        Assert.Equal(observed.Status == CustomLoopRunStatus.Admitted ? admitted.LifecycleVersion : running.LifecycleVersion, observed.LifecycleVersion);
        Assert.Equal(CustomLoopRunStatus.Running, (await store.GetAsync(admitted.Id))!.Status);
    }

    [Fact]
    public async Task Get_exhausts_recognized_contention_with_the_original_io_evidence_and_exact_attempt_budget()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        await WriteDirectAsync(paths, run);
        var contention = CreateRecognizedTransientIOException();
        var beforeOpenCalls = 0;
        Func<CustomLoopRunReadBoundary, string, CancellationToken, ValueTask> rejectRead = (boundary, _, _) =>
        {
            if (boundary == CustomLoopRunReadBoundary.BeforeCanonicalArtifactReadOpen)
            {
                Interlocked.Increment(ref beforeOpenCalls);
                throw contention;
            }

            return ValueTask.CompletedTask;
        };

        using var store = new CustomLoopRunStore(paths, timeProvider: null, artifactReadObserver: rejectRead);
        var exception = await Assert.ThrowsAsync<IOException>(() => store.GetAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(contention, exception);
        Assert.Equal(9, beforeOpenCalls);
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.Read, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
    }

    [Fact]
    public async Task Get_cancellation_during_contention_retry_delay_stops_before_another_attempt_and_allows_a_later_read()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        await WriteDirectAsync(paths, run);
        using var cancellation = new CancellationTokenSource();
        var contention = CreateRecognizedTransientIOException();
        var beforeOpenCalls = 0;
        Func<CustomLoopRunReadBoundary, string, CancellationToken, ValueTask> observeRead = (boundary, _, _) =>
        {
            if (boundary == CustomLoopRunReadBoundary.BeforeCanonicalArtifactReadOpen && Interlocked.Increment(ref beforeOpenCalls) == 1)
            {
                cancellation.Cancel();
                throw contention;
            }

            return ValueTask.CompletedTask;
        };

        using var store = new CustomLoopRunStore(paths, timeProvider: null, artifactReadObserver: observeRead);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAsync(run.Id, cancellation.Token));
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.Read, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
        Assert.Equal(1, beforeOpenCalls);

        AssertRun(run, await store.GetAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, beforeOpenCalls);
    }

    [Fact]
    public async Task Windows_get_cancellation_during_sharing_retry_allows_a_later_evidence_read()
    {
        // FileShare enforcement is Windows-specific; other platforms cannot exercise this contract.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun();
        await WriteDirectAsync(paths, run);
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json");
        using var store = new CustomLoopRunStore(paths);
        using var cancellation = new CancellationTokenSource();

        await using (var externalWriter = new FileStream(artifactPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var pendingRead = store.GetAsync(run.Id, cancellation.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            Assert.False(pendingRead.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingRead);
        }

        AssertRun(run, await store.GetAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Get_does_not_retry_malformed_or_unrecognized_io_evidence_failures()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var malformed = CreateRun("loop-malformed", "run-malformed", "invoke-malformed");
        await WriteRawAsync(paths, malformed.LoopId, malformed.Id, "{invalid");
        using (var malformedStore = new CustomLoopRunStore(paths))
        {
            await Assert.ThrowsAsync<FormatException>(() => malformedStore.GetAsync(malformed.Id));
        }

        var run = CreateRun();
        await WriteDirectAsync(paths, run);
        var beforeOpenCalls = 0;
        Func<CustomLoopRunReadBoundary, string, CancellationToken, ValueTask> rejectRead = (boundary, _, _) =>
        {
            if (boundary == CustomLoopRunReadBoundary.BeforeCanonicalArtifactReadOpen)
            {
                Interlocked.Increment(ref beforeOpenCalls);
                throw new IOException("The reader encountered a non-contention I/O failure.");
            }

            return ValueTask.CompletedTask;
        };

        using var rejectedStore = new CustomLoopRunStore(paths, timeProvider: null, artifactReadObserver: rejectRead);
        var exception = await Assert.ThrowsAsync<IOException>(() => rejectedStore.GetAsync(run.Id));

        Assert.Equal(1, beforeOpenCalls);
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.Read, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
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
    public async Task Get_reconciles_a_displaced_canonical_parent_before_accepting_a_run()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-read-parent", "run-read-parent", "invoke-read-parent");
        await WriteDirectAsync(paths, run);
        var canonicalDirectory = Path.Combine(paths.CustomLoopRunsPath, run.LoopId);
        var displacedDirectory = workspace.File("displaced-read-parent");
        var reconciliationCount = 0;
        var displaced = 0;
        Func<CustomLoopRunReadBoundary, string, CancellationToken, ValueTask> observeRead = (boundary, _, _) =>
        {
            if (boundary == CustomLoopRunReadBoundary.BeforeCanonicalArtifactReadOpen && Interlocked.Exchange(ref displaced, 1) == 0)
            {
                Directory.Move(canonicalDirectory, displacedDirectory);
            }
            else if (boundary == CustomLoopRunReadBoundary.AfterCanonicalArtifactReadMiss && Interlocked.Increment(ref reconciliationCount) == 2)
            {
                Directory.Move(displacedDirectory, canonicalDirectory);
            }

            return ValueTask.CompletedTask;
        };

        using var store = new CustomLoopRunStore(paths, timeProvider: null, artifactReadObserver: observeRead);
        AssertRun(run, await store.GetAsync(run.Id));

        Assert.Equal(2, reconciliationCount);
        Assert.True(File.Exists(Path.Combine(canonicalDirectory, run.Id + ".json")));
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
        using (var restrictiveReader = await WindowsRestrictiveReaderProcess.StartAsync(indexPath, workspace.RootPath))
        {
            var replacementWindow = Stopwatch.StartNew();
            // This lower bound rules out an immediate bypass in the retained contention scenario; it does not claim
            // to isolate atomic-move time from the public CreateAsync work. The separate five-second outer guard
            // bounds that whole public operation with hosted scheduling margin and catches material budget regressions.
            var result = await store.CreateAsync(CreateRun("loop-derived-index", "run-derived-index", "invoke-derived-index")).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CustomLoopRunStoreStatus.Created, result.Status);
            Assert.True(replacementWindow.Elapsed >= TimeSpan.FromMilliseconds(1500), "The derived-index replacement completed without consuming the bounded contention budget.");
            Assert.True(File.Exists(pendingPath));
            await restrictiveReader.ReleaseAsync();
        }

        Assert.Equal(CustomLoopRunStatus.Admitted, (await store.GetAsync("run-derived-index"))!.Status);
        var repaired = await store.ListPageAsync(new CustomLoopRunPageRequest(50));
        Assert.Equal(2, repaired.Items.Count);
        Assert.Contains(repaired.Items, item => item.LoopId == "loop-alpha" && item.Id == "run-alpha");
        Assert.Contains(repaired.Items, item => item.LoopId == "loop-derived-index" && item.Id == "run-derived-index");
        Assert.False(File.Exists(pendingPath));
    }

    [Fact]
    public async Task Windows_restrictive_trace_operation_reader_exhausts_the_auxiliary_replace_budget_and_retry_repairs_the_ledger()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun("loop-operation-contention", "run-operation-contention", "invoke-operation-contention");
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, admitted.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(completed, running.LifecycleVersion)).Status);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(completed.Id));
        var request = new CustomLoopTraceDeletionRequest(completed.Id, inspection.PersistedArtifactHash, "delete-operation-contention", "actor-user", "web");
        var mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), _timestamp.AddMinutes(6));
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(mutation)).Status);
        var operationPath = Path.Combine(paths.CustomLoopTraceDeletionOperationsPath, mutation.Request.OperationId + ".json");

        using (var restrictiveReader = await WindowsRestrictiveReaderProcess.StartAsync(operationPath, workspace.RootPath))
        {
            var replacementWindow = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<IOException>(() => store.MarkTraceDeletionOutcomeAsync(mutation.Request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(replacementWindow.Elapsed >= TimeSpan.FromMilliseconds(1500), "The trace-deletion operation replacement did not consume its bounded auxiliary contention budget.");
            await restrictiveReader.ReleaseAsync();
        }

        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.Marked, await store.MarkTraceDeletionOutcomeAsync(mutation.Request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted));
        var operation = Assert.IsType<CustomLoopTraceDeletionOperation>((await store.GetTraceDeletionOperationAsync(mutation.Request.OperationId)).Operation);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted, operation.Integrity);
    }

    [Fact]
    public async Task Windows_paused_consumer_continuation_preserves_the_auxiliary_replace_failure_after_its_deadline()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun("loop-paused-operation", "run-paused-operation", "invoke-paused-operation");
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, admitted.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(completed, running.LifecycleVersion)).Status);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(completed.Id));
        var request = new CustomLoopTraceDeletionRequest(completed.Id, inspection.PersistedArtifactHash, "delete-paused-operation", "actor-user", "web");
        var mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), _timestamp.AddMinutes(7));
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(mutation)).Status);
        var operationPath = Path.Combine(paths.CustomLoopTraceDeletionOperationsPath, mutation.Request.OperationId + ".json");
        var stagingPattern = $".{Path.GetFileName(operationPath)}.*.tmp";
        var gated = new QueuedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task<CustomLoopTraceDeletionAuditMarkStatus>? markTask = null;

        using (var restrictiveReader = await WindowsRestrictiveReaderProcess.StartAsync(operationPath, workspace.RootPath))
        {
            SynchronizationContext.SetSynchronizationContext(gated);
            try
            {
                markTask = store.MarkTraceDeletionOutcomeAsync(mutation.Request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            var stagingWait = Stopwatch.StartNew();
            while (!Directory.EnumerateFiles(paths.CustomLoopTraceDeletionOperationsPath, stagingPattern).Any())
            {
                Assert.False(markTask.IsCompleted, "The trace-deletion operation did not reach its staged replacement boundary.");
                Assert.True(stagingWait.Elapsed < TimeSpan.FromSeconds(10), "The trace-deletion operation did not reach its staged replacement boundary within the bounded wait.");
                gated.Drain();
                await Task.Delay(TimeSpan.FromMilliseconds(15));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
            gated.Drain();
            await Task.Delay(TimeSpan.FromMilliseconds(2200));
            await gated.DrainUntilCompletedAsync(markTask, TimeSpan.FromSeconds(5));
            var exception = await Assert.ThrowsAnyAsync<IOException>(() => markTask);
            Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.Unknown, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
            await restrictiveReader.ReleaseAsync();
        }

        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.Marked, await store.MarkTraceDeletionOutcomeAsync(mutation.Request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted));
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

    [Theory]
    [InlineData(".custom-loop-run-index.json~RF55ffa.TMP")]
    [InlineData(".custom-loop-run-index.json~RF100810.TMP")]
    public async Task Canonical_run_reads_ignore_and_mutation_recovery_reclaims_bounded_windows_discovery_index_replace_temporaries(string fileName)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = At(CreateRun("loop-alpha", "run-alpha", "invoke-alpha"), 1);
        await WriteDirectAsync(paths, run);
        var replacementTemporaryPath = Path.Combine(paths.CustomLoopRunsPath, fileName);
        await File.WriteAllTextAsync(replacementTemporaryPath, "replace-in-progress");
        using var store = new CustomLoopRunStore(paths);

        AssertRun(run, await store.GetAsync(run.Id));
        Assert.True(File.Exists(replacementTemporaryPath));

        var repaired = await store.ListPageAsync(new CustomLoopRunPageRequest(50));

        Assert.Equal(run.Id, Assert.Single(repaired.Items).Id);
        Assert.False(File.Exists(replacementTemporaryPath));
        AssertRun(run, await store.GetAsync(run.Id));
    }

    [Theory]
    [InlineData(".custom-loop-run-index.json~RF55ff.TMP")]
    [InlineData(".custom-loop-run-index.json~RF1008100.TMP")]
    [InlineData(".custom-loop-run-index.json~RF10081A.TMP")]
    [InlineData(".custom-loop-run-index.json~RF10081g.TMP")]
    [InlineData(".custom-loop-run-index.json~Rf100810.TMP")]
    [InlineData(".custom-loop-run-index.json~RF100810.tmp")]
    [InlineData(".custom-loop-run-index.json~RF100810.TMPx")]
    [InlineData(".custom-loop-run-index.pending~RF100810.TMP")]
    [InlineData(".unrelated.json~RF100810.TMP")]
    public async Task Root_discovery_index_replace_temporary_lookalikes_fail_closed_with_filename_evidence(string fileName)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = At(CreateRun("loop-alpha", "run-alpha", "invoke-alpha"), 1);
        await WriteDirectAsync(paths, run);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopRunsPath, fileName), "unexpected");

        var exception = await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync(run.Id));

        Assert.Contains($"Artifacts=[\"{fileName}\"]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canonical_run_replace_temporary_lookalike_is_not_accepted_as_a_root_discovery_index_temporary()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = At(CreateRun("loop-alpha", "run-alpha", "invoke-alpha"), 1);
        await WriteDirectAsync(paths, run);
        var replacementTemporaryPath = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json~RF100810.TMP");
        await File.WriteAllTextAsync(replacementTemporaryPath, "unexpected");

        var exception = await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync(run.Id));

        Assert.Contains(Path.GetFileName(replacementTemporaryPath), exception.Message, StringComparison.Ordinal);
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
    public async Task Windows_run_page_uses_canonical_evidence_when_the_mutation_lease_is_read_only()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-read-only-page", "run-read-only-page", "invoke-read-only-page");
        await WriteDirectAsync(paths, run);
        using var store = new CustomLoopRunStore(paths);
        Assert.Equal(run.Id, Assert.Single((await store.ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Id);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var lockPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-runs.lock");
        File.Delete(indexPath);
        var originalAttributes = File.GetAttributes(lockPath);
        try
        {
            File.SetAttributes(lockPath, originalAttributes | FileAttributes.ReadOnly);

            var page = await store.ListPageAsync(new CustomLoopRunPageRequest(50));

            Assert.Equal(run.Id, Assert.Single(page.Items).Id);
            Assert.False(File.Exists(indexPath));
        }
        finally
        {
            File.SetAttributes(lockPath, originalAttributes);
        }
    }

    [Fact]
    public async Task Canonical_publication_rebuilds_derived_index_when_an_existing_artifact_changes_after_target_proof()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var original = CreateRun("loop-index-drift-original", "run-index-drift-original", "invoke-index-drift-original");
        var externallyRewritten = CustomLoopAdmissionRequestHash.Apply(original with { TriggerPrompt = new string('x', 97), AdmissionRequestHash = string.Empty });
        var rewrittenContent = CustomLoopRunArtifactSerializer.Serialize(externallyRewritten);
        var originalPath = Path.Combine(paths.CustomLoopRunsPath, original.LoopId, original.Id + ".json");
        using (var established = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await established.CreateAsync(original)).Status);
        }

        var rewriteCount = 0;
        using var store = new CustomLoopRunStore(paths, null, async (boundary, cancellationToken) =>
        {
            if (boundary == CustomLoopRunPublicationBoundary.TargetProven && Interlocked.Exchange(ref rewriteCount, 1) == 0)
            {
                await File.WriteAllBytesAsync(originalPath, rewrittenContent, cancellationToken);
            }
        });
        var published = CreateRun("loop-index-drift-published", "run-index-drift-published", "invoke-index-drift-published");

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(published)).Status);
        Assert.Equal(1, rewriteCount);
        var page = await store.ListPageAsync(new CustomLoopRunPageRequest(50));
        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, item => item.Id == original.Id);
        Assert.Contains(page.Items, item => item.Id == published.Id);
        var index = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json")))!.AsObject();
        var rewrittenEntry = index["entries"]!.AsArray().Single(entry => entry!["summary"]!["id"]!.GetValue<string>() == original.Id)!.AsObject();
        Assert.Equal(rewrittenContent.Length, rewrittenEntry["artifactUtf8Bytes"]!.GetValue<int>());
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
            root => root.Remove("humanInputWaitingCheckpoints"),
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
    public async Task Update_returns_limit_exceeded_for_a_valid_successor_that_exhausts_reserved_trace_capacity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var blocks = Enumerable.Range(0, 46).Select(index => CapacityContextBlock(index, "source-current")).ToArray();
        var initial = CreateRun();
        var current = initial with { Events = [initial.Events[0] with { ContextBlocks = blocks }] };
        using var store = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(current)).Status);
        var appendedBlocks = new[] { CapacityContextBlock(0, "source-successor"), CapacityContextBlock(1, "source-successor") };
        var checkpoint = new CustomLoopRunEvent(
            current.Events.LongLength + 1,
            "event-reserved-capacity-exhausted",
            current.UpdatedAtUtc,
            CustomLoopRunEventKind.CheckpointCommitted,
            1,
            null,
            null,
            "Checkpoint evidence exhausted the remaining reserved trace capacity.",
            appendedBlocks,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var candidate = current with
        {
            LifecycleVersion = current.LifecycleVersion + 1,
            Events = [.. current.Events, checkpoint]
        };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(current, candidate).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(current, candidate).Errors));
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, current.LoopId, current.Id + ".json");
        var preservedBytes = await File.ReadAllBytesAsync(artifactPath);

        var result = await store.UpdateAsync(candidate, current.LifecycleVersion);

        Assert.Equal(CustomLoopRunStoreStatus.LimitExceeded, result.Status);
        Assert.Equal(preservedBytes, await File.ReadAllBytesAsync(artifactPath));
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

    private static CustomLoopContextBlock CapacityContextBlock(int index, string prefix)
    {
        var label = $"{prefix}-{index:D2}:";
        var content = label + new string('x', CustomLoopLimits.MaxLogicalProviderRequestCharacters - label.Length);
        return new CustomLoopContextBlock(CustomLoopContextSource.HarnessGovernance, $"{prefix}-{index:D2}", LlmMessageRole.System, true, null, content, CustomLoopTraceContentHash.Compute(content), content.Length, false, EmbodySenseDeveloperInstructions.CurrentVersion);
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

    [Fact]
    public async Task Terminal_integrity_warning_refuses_an_ambiguous_canonical_run_identity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = CreateRun("loop-warning-alpha", "run-warning-duplicate", "invoke-warning-alpha");
        var second = CreateRun("loop-warning-beta", "run-warning-duplicate", "invoke-warning-beta");
        await WriteDirectAsync(paths, first);
        await WriteDirectAsync(paths, second);
        var warning = Event(2, "warning-duplicate", CustomLoopRunEventKind.IntegrityWarning, _timestamp.AddMinutes(1));

        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).AppendTerminalIntegrityWarningAsync(first.Id, first.LifecycleVersion, warning));

        Assert.Equal(2, Directory.EnumerateFiles(paths.CustomLoopRunsPath, "*.json", SearchOption.AllDirectories).Count());
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
            var exception = await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync("run-alpha"));
            Assert.Contains("Artifacts=[\"unexpected-root-artifact\"]", exception.Message, StringComparison.Ordinal);
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
    public async Task Trace_deletion_audit_mark_fails_closed_when_its_tombstone_no_longer_matches_the_operation_ledger()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun("loop-tombstone-ledger", "run-tombstone-ledger", "invoke-tombstone-ledger");
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, admitted.LifecycleVersion)).Status);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(completed, running.LifecycleVersion)).Status);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(completed.Id));
        var request = new CustomLoopTraceDeletionRequest(completed.Id, inspection.PersistedArtifactHash, "delete-tombstone-ledger", "actor-user", "web");
        var mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), _timestamp.AddMinutes(3));
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(mutation)).Status);
        var tombstone = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(completed.Id)).Tombstone!;
        var differentOperationId = "different-operation";
        var mismatchedRequest = new CustomLoopTraceDeletionRequest(tombstone.RunId, tombstone.OriginalTraceHash, differentOperationId, tombstone.DeletionActor, tombstone.DeletionSurface);
        var mismatched = tombstone with
        {
            DeletionOperationId = differentOperationId,
            DeletionRequestHash = CustomLoopTraceDeletionRequestHash.Compute(mismatchedRequest),
            IntentAuditCorrelationId = differentOperationId,
            OutcomeAuditCorrelationId = differentOperationId
        };
        var tracePath = Path.Combine(paths.CustomLoopRunsPath, completed.LoopId, completed.Id + ".json");
        await File.WriteAllTextAsync(tracePath, JsonSerializer.Serialize(mismatched, _artifactJsonOptions) + "\n");

        var exception = await Assert.ThrowsAsync<FormatException>(() => store.MarkTraceDeletionOutcomeAsync(mutation.Request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted));

        Assert.Contains("no longer matches its durable operation ledger", exception.Message, StringComparison.Ordinal);
        var operation = Assert.IsType<CustomLoopTraceDeletionOperation>((await store.GetTraceDeletionOperationAsync(mutation.Request.OperationId)).Operation);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit, operation.Integrity);
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

    [Fact]
    public async Task Canonical_run_and_tombstone_publication_acknowledge_only_after_flush_rename_parent_barrier_and_target_proof()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var observed = new List<CustomLoopRunPublicationBoundary>();
        using var store = new CustomLoopRunStore(paths, null, (boundary, _) =>
        {
            observed.Add(boundary);
            return ValueTask.CompletedTask;
        });
        var admitted = CreateRun("loop-durable", "run-durable", "invoke-durable");

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, admitted.LifecycleVersion)).Status);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(completed, running.LifecycleVersion)).Status);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(completed.Id));
        var request = new CustomLoopTraceDeletionRequest(completed.Id, inspection.PersistedArtifactHash, "delete-durable", "actor-user", "web");
        var mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), _timestamp.AddMinutes(3));
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(mutation)).Status);

        var expected = new[]
        {
            CustomLoopRunPublicationBoundary.StagedFileFlushed,
            CustomLoopRunPublicationBoundary.CanonicalRenamed,
            CustomLoopRunPublicationBoundary.ParentDirectoryFlushed,
            CustomLoopRunPublicationBoundary.TargetProven
        };
        Assert.Equal(expected, observed.Take(4));
        Assert.Equal(expected, observed.Skip(4).Take(4));
        Assert.Equal(expected, observed.Skip(8).Take(4));
        Assert.Equal(expected, observed.Skip(12).Take(4));
        Assert.Equal(16, observed.Count);
    }

    [Fact]
    public async Task Windows_fixed_local_ntfs_canonical_publication_completes_the_native_success_path()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        var run = CreateRun("loop-windows-native", "run-windows-native", "invoke-windows-native");

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(run)).Status);
        var updated = Advance(run, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(updated, run.LifecycleVersion)).Status);
        AssertRun(updated, await store.GetAsync(updated.Id));
    }

    [Fact]
    public async Task MacOS_canonical_publication_uses_full_native_durability_barriers_for_run_and_tombstone()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        var admitted = CreateRun("loop-macos-full-sync", "run-macos-full-sync", "invoke-macos-full-sync");

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, admitted.LifecycleVersion)).Status);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(completed, running.LifecycleVersion)).Status);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await store.InspectTraceAsync(completed.Id));
        var request = new CustomLoopTraceDeletionRequest(completed.Id, inspection.PersistedArtifactHash, "delete-macos-full-sync", "actor-user", "web");
        var mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), _timestamp.AddMinutes(3));
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await store.DeleteTerminalTraceAsync(mutation)).Status);

        using var restarted = new CustomLoopRunStore(paths);
        var tombstone = Assert.IsType<CustomLoopTraceInspection>(await restarted.InspectTraceAsync(completed.Id));
        Assert.Equal(CustomLoopTraceArtifactKind.Tombstone, tombstone.Kind);
        Assert.Null(await restarted.GetAsync(completed.Id));
    }

    [Fact]
    public async Task Create_reuses_a_preexisting_empty_canonical_loop_directory_and_remains_idempotent_after_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-preexisting-directory", "run-preexisting-directory", "invoke-preexisting-directory");
        var loopDirectory = Path.Combine(paths.CustomLoopRunsPath, run.LoopId);
        Directory.CreateDirectory(loopDirectory);

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await new CustomLoopRunStore(paths).CreateAsync(run)).Status);
        AssertRun(run, await new CustomLoopRunStore(paths).GetAsync(run.Id));
        Assert.Equal(CustomLoopRunStoreStatus.AlreadyCreated, (await new CustomLoopRunStore(paths).CreateAsync(run)).Status);
        Assert.Single(Directory.EnumerateFiles(loopDirectory, "*.json"));
    }

    [Fact]
    public async Task Create_fails_closed_when_new_canonical_run_ancestry_is_a_reparse_point()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var outside = workspace.File("outside-canonical-runs");
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

        await Assert.ThrowsAsync<IOException>(() => new CustomLoopRunStore(paths).CreateAsync(CreateRun("loop-reparse-ancestry", "run-reparse-ancestry", "invoke-reparse-ancestry")));
        Assert.Empty(Directory.EnumerateFiles(outside, "*.json"));
    }

    [Fact]
    public async Task Windows_canonical_publication_refuses_reparse_ancestry_and_post_rename_reparse_target_without_acknowledging_discovery()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var outside = workspace.File("outside-canonical-runs");
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.CustomLoopRunsPath)!);
            Directory.CreateSymbolicLink(paths.CustomLoopRunsPath, outside);

            await Assert.ThrowsAsync<IOException>(() => new CustomLoopRunStore(paths).CreateAsync(CreateRun("loop-windows-reparse-ancestry", "run-windows-reparse-ancestry", "invoke-windows-reparse-ancestry")));
            Assert.Empty(Directory.EnumerateFiles(outside, "*.json"));
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var run = CreateRun("loop-windows-reparse-target", "run-windows-reparse-target", "invoke-windows-reparse-target");
            var artifactPath = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json");
            var outside = workspace.File("outside-canonical-target");
            await File.WriteAllTextAsync(outside, "outside");
            var reparseCreated = false;
            using var store = new CustomLoopRunStore(paths, null, (boundary, _) =>
            {
                if (boundary == CustomLoopRunPublicationBoundary.ParentDirectoryFlushed)
                {
                    File.Delete(artifactPath);
                    File.CreateSymbolicLink(artifactPath, outside);
                    reparseCreated = true;
                }

                return ValueTask.CompletedTask;
            });

            var exception = await Assert.ThrowsAnyAsync<IOException>(() => store.CreateAsync(run));

            Assert.True(reparseCreated);
            Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
            Assert.True(File.GetAttributes(artifactPath).HasFlag(FileAttributes.ReparsePoint));
            Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
            Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json")));
        }
    }

    [Fact]
    public async Task Separate_process_loss_after_staged_flush_on_first_run_preserves_the_canonical_directory_and_allows_one_retry()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var expected = CreateRun("loop-process-loss", "run-process-loss", "invoke-process-loss");
        using var process = CancellationHostProcess.Start("custom-loop-run-process-loss", workspace.RootPath, CustomLoopRunPublicationBoundary.StagedFileFlushed.ToString());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode != 0, $"Process-loss worker unexpectedly completed normally. stdout: {output} stderr: {error}");
        Assert.Contains("test host process crashed", error, StringComparison.OrdinalIgnoreCase);
        var loopDirectory = Path.Combine(paths.CustomLoopRunsPath, expected.LoopId);
        Assert.True(Directory.Exists(loopDirectory));
        Assert.Empty(Directory.EnumerateFiles(loopDirectory, "*.json"));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json")));

        using var restarted = new CustomLoopRunStore(paths);
        Assert.Null(await restarted.GetAsync(expected.Id));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await restarted.CreateAsync(expected)).Status);
        AssertRun(expected, await restarted.GetAsync(expected.Id));
        Assert.Single(Directory.EnumerateFiles(loopDirectory, "*.json"));
    }

    [Fact]
    public async Task Separate_process_loss_after_staged_flush_reuses_a_preexisting_loop_directory_and_allows_one_retry()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var expected = CreateRun("loop-process-loss", "run-process-loss", "invoke-process-loss");
        var loopDirectory = Path.Combine(paths.CustomLoopRunsPath, expected.LoopId);
        Directory.CreateDirectory(loopDirectory);
        using var process = CancellationHostProcess.Start("custom-loop-run-process-loss", workspace.RootPath, CustomLoopRunPublicationBoundary.StagedFileFlushed.ToString());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode != 0, $"Process-loss worker unexpectedly completed normally. stdout: {output} stderr: {error}");
        Assert.Contains("test host process crashed", error, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(loopDirectory));
        Assert.Empty(Directory.EnumerateFiles(loopDirectory, "*.json"));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json")));

        using var restarted = new CustomLoopRunStore(paths);
        Assert.Null(await restarted.GetAsync(expected.Id));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await restarted.CreateAsync(expected)).Status);
        AssertRun(expected, await restarted.GetAsync(expected.Id));
        Assert.Single(Directory.EnumerateFiles(loopDirectory, "*.json"));
    }

    [Theory]
    [InlineData(CustomLoopRunPublicationBoundary.CanonicalRenamed)]
    [InlineData(CustomLoopRunPublicationBoundary.TargetProven)]
    public async Task Separate_process_loss_after_canonical_rename_or_proof_preserves_one_run_without_derived_acknowledgement(CustomLoopRunPublicationBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var expected = CreateRun("loop-process-loss", "run-process-loss", "invoke-process-loss");
        using var process = CancellationHostProcess.Start("custom-loop-run-process-loss", workspace.RootPath, boundary.ToString());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode != 0, $"Process-loss worker unexpectedly completed normally. stdout: {output} stderr: {error}");
        Assert.Contains("test host process crashed", error, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json")));
        var artifacts = Directory.EnumerateFiles(Path.Combine(paths.CustomLoopRunsPath, expected.LoopId), "*.json").ToArray();
        Assert.Single(artifacts);

        using var restarted = new CustomLoopRunStore(paths);
        AssertRun(expected, await restarted.GetAsync(expected.Id));
        Assert.Equal(CustomLoopRunStoreStatus.AlreadyCreated, (await restarted.CreateAsync(expected)).Status);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(paths.CustomLoopRunsPath, expected.LoopId), "*.json"));
    }

    [Fact]
    public async Task Replaced_canonical_parent_after_target_proof_is_unknown_and_preserves_the_displaced_possible_winner()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-parent-replacement", "run-parent-replacement", "invoke-parent-replacement");
        var canonicalDirectory = Path.Combine(paths.CustomLoopRunsPath, run.LoopId);
        var displacedDirectory = workspace.File("displaced-loop-parent");
        using var store = new CustomLoopRunStore(paths, null, (boundary, _) =>
        {
            if (boundary == CustomLoopRunPublicationBoundary.TargetProven)
            {
                Directory.Move(canonicalDirectory, displacedDirectory);
                Directory.CreateDirectory(canonicalDirectory);
            }

            return ValueTask.CompletedTask;
        });

        var exception = await Assert.ThrowsAnyAsync<IOException>(() => store.CreateAsync(run));
        var diagnostic = Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception));
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, diagnostic.Stage);
        Assert.DoesNotContain(workspace.RootPath, exception.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(displacedDirectory, run.Id + ".json")));
        Assert.False(File.Exists(Path.Combine(canonicalDirectory, run.Id + ".json")));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json")));

        using var restarted = new CustomLoopRunStore(paths);
        Assert.Null(await restarted.GetAsync(run.Id));
        Assert.Single(Directory.EnumerateFiles(displacedDirectory, "*.json"));
    }

    [Fact]
    public async Task Missing_canonical_parent_after_target_proof_is_unknown_and_preserves_the_displaced_possible_winner()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-parent-missing", "run-parent-missing", "invoke-parent-missing");
        var canonicalDirectory = Path.Combine(paths.CustomLoopRunsPath, run.LoopId);
        var displacedDirectory = workspace.File("missing-loop-parent");
        var parentRemoved = false;
        using var store = new CustomLoopRunStore(paths, null, (boundary, _) =>
        {
            if (boundary == CustomLoopRunPublicationBoundary.TargetProven)
            {
                Directory.Move(canonicalDirectory, displacedDirectory);
                parentRemoved = true;
            }

            return ValueTask.CompletedTask;
        });

        var exception = await Assert.ThrowsAnyAsync<IOException>(() => store.CreateAsync(run));

        Assert.True(parentRemoved);
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
        Assert.True(File.Exists(Path.Combine(displacedDirectory, run.Id + ".json")));
        Assert.False(Directory.Exists(canonicalDirectory));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json")));
    }

    [Fact]
    public async Task Reparse_canonical_parent_after_target_proof_is_unknown_without_acknowledging_discovery()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-parent-reparse", "run-parent-reparse", "invoke-parent-reparse");
        var canonicalDirectory = Path.Combine(paths.CustomLoopRunsPath, run.LoopId);
        var displacedDirectory = workspace.File("reparse-loop-parent");
        var reparseCreated = false;
        using var store = new CustomLoopRunStore(paths, null, (boundary, _) =>
        {
            if (boundary == CustomLoopRunPublicationBoundary.TargetProven)
            {
                Directory.Move(canonicalDirectory, displacedDirectory);
                Directory.CreateSymbolicLink(canonicalDirectory, displacedDirectory);
                reparseCreated = true;
            }

            return ValueTask.CompletedTask;
        });

        var exception = await Assert.ThrowsAnyAsync<IOException>(() => store.CreateAsync(run));

        Assert.True(reparseCreated);
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
        Assert.True(File.Exists(Path.Combine(displacedDirectory, run.Id + ".json")));
        Assert.True(File.GetAttributes(canonicalDirectory).HasFlag(FileAttributes.ReparsePoint));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json")));
    }

    [Fact]
    public async Task Directory_substitution_after_canonical_rename_is_unknown_and_never_acknowledges_discovery()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-target-directory", "run-target-directory", "invoke-target-directory");
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json");
        var directorySubstituted = false;
        using var store = new CustomLoopRunStore(paths, null, (boundary, _) =>
        {
            if (boundary == CustomLoopRunPublicationBoundary.CanonicalRenamed)
            {
                File.Delete(artifactPath);
                Directory.CreateDirectory(artifactPath);
                directorySubstituted = true;
            }

            return ValueTask.CompletedTask;
        });

        var exception = await Assert.ThrowsAnyAsync<IOException>(() => store.CreateAsync(run));

        Assert.True(directorySubstituted);
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
        Assert.True(Directory.Exists(artifactPath));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json")));
    }

    [Fact]
    public async Task Missing_target_after_canonical_rename_is_unknown_and_never_acknowledges_discovery()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-target-missing", "run-target-missing", "invoke-target-missing");
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json");
        var deleted = false;
        using (var failing = new CustomLoopRunStore(paths, null, (boundary, _) =>
        {
            if (boundary == CustomLoopRunPublicationBoundary.CanonicalRenamed)
            {
                File.Delete(artifactPath);
                deleted = true;
            }

            return ValueTask.CompletedTask;
        }))
        {
            var exception = await Assert.ThrowsAnyAsync<IOException>(() => failing.CreateAsync(run));
            Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
            Assert.DoesNotContain(workspace.RootPath, exception.ToString(), StringComparison.Ordinal);
        }

        Assert.True(deleted);
        Assert.False(File.Exists(artifactPath));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json")));
        using var restarted = new CustomLoopRunStore(paths);
        Assert.Null(await restarted.GetAsync(run.Id));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await restarted.CreateAsync(run)).Status);
        AssertRun(run, await restarted.GetAsync(run.Id));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(paths.CustomLoopRunsPath, run.LoopId), "*.json"));
    }

    [Fact]
    public async Task Reparse_target_substitution_after_directory_barrier_is_unknown_and_never_acknowledges_discovery()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-target-reparse", "run-target-reparse", "invoke-target-reparse");
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json");
        var target = workspace.File("reparse-target");
        await File.WriteAllTextAsync(target, "outside");
        var reparseCreated = false;
        using var store = new CustomLoopRunStore(paths, null, (boundary, _) =>
        {
            if (boundary == CustomLoopRunPublicationBoundary.ParentDirectoryFlushed)
            {
                File.Delete(artifactPath);
                File.CreateSymbolicLink(artifactPath, target);
                reparseCreated = true;
            }

            return ValueTask.CompletedTask;
        });

        var exception = await Assert.ThrowsAnyAsync<IOException>(() => store.CreateAsync(run));

        Assert.True(reparseCreated);
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
        Assert.True(File.GetAttributes(artifactPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json")));
    }

    [Fact]
    public async Task Hard_linked_target_after_directory_barrier_is_unknown_and_never_acknowledges_discovery()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-target-hard-link", "run-target-hard-link", "invoke-target-hard-link");
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json");
        var aliasPath = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, ".target-link");
        var hardLinkCreated = false;
        using var store = new CustomLoopRunStore(paths, null, (boundary, _) =>
        {
            if (boundary == CustomLoopRunPublicationBoundary.ParentDirectoryFlushed)
            {
                CreateHardLink(aliasPath, artifactPath);
                hardLinkCreated = true;
            }

            return ValueTask.CompletedTask;
        });

        var exception = await Assert.ThrowsAnyAsync<IOException>(() => store.CreateAsync(run));

        Assert.True(hardLinkCreated);
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
        Assert.True(File.Exists(aliasPath));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json")));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("length")]
    [InlineData("content")]
    public async Task Target_proof_rejects_identity_length_and_content_substitution_after_the_directory_barrier(string substitution)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-target-proof", "run-target-proof", "invoke-target-proof");
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, run.LoopId, run.Id + ".json");
        byte[]? expectedContent = null;
        using var store = new CustomLoopRunStore(paths, null, (boundary, _) =>
        {
            if (boundary == CustomLoopRunPublicationBoundary.ParentDirectoryFlushed)
            {
                expectedContent = File.ReadAllBytes(artifactPath);
                var replacementContent = expectedContent.ToArray();
                switch (substitution)
                {
                    case "identity":
                        File.Delete(artifactPath);
                        File.WriteAllBytes(artifactPath, replacementContent);
                        break;
                    case "length":
                        File.WriteAllBytes(artifactPath, replacementContent[..^1]);
                        break;
                    case "content":
                        replacementContent[replacementContent.Length / 2] ^= 0x01;
                        File.WriteAllBytes(artifactPath, replacementContent);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(substitution));
                }
            }

            return ValueTask.CompletedTask;
        });

        var exception = await Assert.ThrowsAnyAsync<IOException>(() => store.CreateAsync(run));

        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception)).Stage);
        Assert.NotNull(expectedContent);
        var observedContent = await File.ReadAllBytesAsync(artifactPath);
        switch (substitution)
        {
            case "identity":
                Assert.Equal(expectedContent, observedContent);
                break;
            case "length":
                Assert.True(observedContent.Length < expectedContent.Length);
                break;
            case "content":
                Assert.Equal(expectedContent.Length, observedContent.Length);
                Assert.False(expectedContent.SequenceEqual(observedContent));
                break;
        }

        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json")));
    }

    [Fact]
    public async Task Discovery_index_directory_after_canonical_proof_leaves_pending_evidence_for_restart_repair()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-index-directory", "run-index-directory", "invoke-index-directory");
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var indexDirectoryCreated = false;
        using var store = new CustomLoopRunStore(paths, null, (boundary, _) =>
        {
            if (boundary == CustomLoopRunPublicationBoundary.TargetProven)
            {
                Directory.CreateDirectory(indexPath);
                indexDirectoryCreated = true;
            }

            return ValueTask.CompletedTask;
        });

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(run)).Status);

        Assert.True(indexDirectoryCreated);
        Assert.True(Directory.Exists(indexPath));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Directory.Delete(indexPath);
        Assert.Equal(run.Id, Assert.Single((await new CustomLoopRunStore(paths).ListPageAsync(new CustomLoopRunPageRequest(50))).Items).Id);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
    }

    [Fact]
    public async Task Post_rename_directory_barrier_failure_preserves_the_possible_winner_leaves_index_pending_and_restarts_idempotently()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-unknown", "run-unknown", "invoke-unknown");
        using var failing = new CustomLoopRunStore(paths, null, (boundary, _) => boundary == CustomLoopRunPublicationBoundary.CanonicalRenamed
            ? ValueTask.FromException(new IOException("Injected post-rename durability barrier failure."))
            : ValueTask.CompletedTask);

        var exception = await Assert.ThrowsAnyAsync<IOException>(() => failing.CreateAsync(run));
        var diagnostic = Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception));
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, diagnostic.Stage);
        Assert.DoesNotContain(workspace.RootPath, exception.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json")));

        using var restarted = new CustomLoopRunStore(paths);
        AssertRun(run, await restarted.GetAsync(run.Id));
        Assert.Equal(CustomLoopRunStoreStatus.AlreadyCreated, (await restarted.CreateAsync(run)).Status);
    }

    [Fact]
    public async Task Post_rename_directory_barrier_failure_during_update_preserves_one_possible_winner_and_does_not_acknowledge_the_index()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = CreateRun("loop-update-unknown", "run-update-unknown", "invoke-update-unknown");
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        using (var established = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await established.CreateAsync(admitted)).Status);
        }

        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var indexBeforeFailure = await File.ReadAllBytesAsync(indexPath);
        using (var failing = new CustomLoopRunStore(paths, null, (boundary, _) => boundary == CustomLoopRunPublicationBoundary.CanonicalRenamed
            ? ValueTask.FromException(new IOException("Injected post-rename update durability failure."))
            : ValueTask.CompletedTask))
        {
            var exception = await Assert.ThrowsAnyAsync<IOException>(() => failing.UpdateAsync(running, admitted.LifecycleVersion));
            var diagnostic = Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception));
            Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, diagnostic.Stage);
            Assert.DoesNotContain(workspace.RootPath, exception.ToString(), StringComparison.Ordinal);
        }

        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.Equal(indexBeforeFailure, await File.ReadAllBytesAsync(indexPath));
        var artifactPaths = Directory.EnumerateFiles(Path.Combine(paths.CustomLoopRunsPath, admitted.LoopId), "*.json").ToArray();
        Assert.Single(artifactPaths);

        using var restarted = new CustomLoopRunStore(paths);
        AssertRun(running, await restarted.GetAsync(running.Id));
        var retry = await restarted.UpdateAsync(running, admitted.LifecycleVersion);
        Assert.Equal(CustomLoopRunStoreStatus.Conflict, retry.Status);
        Assert.Equal(running.LifecycleVersion, retry.Conflict!.ActualLifecycleVersion);
    }

    [Fact]
    public async Task Post_rename_directory_barrier_failure_during_tombstone_preserves_one_tombstone_and_retries_the_pending_operation_safely()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = CreateRun("loop-tombstone-unknown", "run-tombstone-unknown", "invoke-tombstone-unknown");
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        CustomLoopTraceInspection inspection;
        using (var established = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await established.CreateAsync(admitted)).Status);
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await established.UpdateAsync(running, admitted.LifecycleVersion)).Status);
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await established.UpdateAsync(completed, running.LifecycleVersion)).Status);
            inspection = Assert.IsType<CustomLoopTraceInspection>(await established.InspectTraceAsync(completed.Id));
        }

        var request = new CustomLoopTraceDeletionRequest(completed.Id, inspection.PersistedArtifactHash, "delete-unknown", "actor-user", "web");
        var mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), _timestamp.AddMinutes(4));
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var indexBeforeFailure = await File.ReadAllBytesAsync(indexPath);
        using (var failing = new CustomLoopRunStore(paths, null, (boundary, _) => boundary == CustomLoopRunPublicationBoundary.CanonicalRenamed
            ? ValueTask.FromException(new IOException("Injected post-rename tombstone durability failure."))
            : ValueTask.CompletedTask))
        {
            var exception = await Assert.ThrowsAnyAsync<IOException>(() => failing.DeleteTerminalTraceAsync(mutation));
            var diagnostic = Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception));
            Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, diagnostic.Stage);
            Assert.DoesNotContain(workspace.RootPath, exception.ToString(), StringComparison.Ordinal);
        }

        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.Equal(indexBeforeFailure, await File.ReadAllBytesAsync(indexPath));
        var artifactPaths = Directory.EnumerateFiles(Path.Combine(paths.CustomLoopRunsPath, completed.LoopId), "*.json").ToArray();
        Assert.Single(artifactPaths);

        using var restarted = new CustomLoopRunStore(paths);
        var tombstone = Assert.IsType<CustomLoopTraceInspection>(await restarted.InspectTraceAsync(completed.Id));
        Assert.Equal(CustomLoopTraceArtifactKind.Tombstone, tombstone.Kind);
        Assert.Equal(mutation.Request.OperationId, tombstone.Tombstone!.DeletionOperationId);
        Assert.Null(await restarted.GetAsync(completed.Id));
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await restarted.DeleteTerminalTraceAsync(mutation)).Status);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.AlreadyDeleted, (await restarted.DeleteTerminalTraceAsync(mutation)).Status);
    }

    [Fact]
    public async Task Post_rename_directory_barrier_failure_during_tombstone_audit_mark_preserves_the_possible_winner_and_retries_safely()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = CreateRun("loop-tombstone-mark-unknown", "run-tombstone-mark-unknown", "invoke-tombstone-mark-unknown");
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        CustomLoopTraceDeletionMutation mutation;
        using (var established = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await established.CreateAsync(admitted)).Status);
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await established.UpdateAsync(running, admitted.LifecycleVersion)).Status);
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await established.UpdateAsync(completed, running.LifecycleVersion)).Status);
            var inspection = Assert.IsType<CustomLoopTraceInspection>(await established.InspectTraceAsync(completed.Id));
            var request = new CustomLoopTraceDeletionRequest(completed.Id, inspection.PersistedArtifactHash, "delete-mark-unknown", "actor-user", "web");
            mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), _timestamp.AddMinutes(5));
            Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await established.DeleteTerminalTraceAsync(mutation)).Status);
        }

        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        var indexBeforeFailure = await File.ReadAllBytesAsync(indexPath);
        using (var failing = new CustomLoopRunStore(paths, null, (boundary, _) => boundary == CustomLoopRunPublicationBoundary.CanonicalRenamed
            ? ValueTask.FromException(new IOException("Injected post-rename tombstone audit-mark durability failure."))
            : ValueTask.CompletedTask))
        {
            var exception = await Assert.ThrowsAnyAsync<IOException>(() => failing.MarkTraceDeletionOutcomeAsync(mutation.Request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted));
            var diagnostic = Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception));
            Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, diagnostic.Stage);
            Assert.DoesNotContain(workspace.RootPath, exception.ToString(), StringComparison.Ordinal);
        }

        Assert.True(File.Exists(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.pending")));
        Assert.Equal(indexBeforeFailure, await File.ReadAllBytesAsync(indexPath));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(paths.CustomLoopRunsPath, completed.LoopId), "*.json"));

        using var restarted = new CustomLoopRunStore(paths);
        var possibleWinner = Assert.IsType<CustomLoopTraceInspection>(await restarted.InspectTraceAsync(completed.Id));
        Assert.Equal(CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted, possibleWinner.Tombstone!.OutcomeIntegrity);
        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.Marked, await restarted.MarkTraceDeletionOutcomeAsync(mutation.Request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted));
        var operation = Assert.IsType<CustomLoopTraceDeletionOperation>((await restarted.GetTraceDeletionOperationAsync(mutation.Request.OperationId)).Operation);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted, operation.Integrity);
    }

    [Fact]
    public async Task Pre_rename_publication_failure_leaves_no_canonical_run_and_retains_a_path_free_canonical_replace_diagnostic()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var run = CreateRun("loop-prerename", "run-prerename", "invoke-prerename");
        using var failing = new CustomLoopRunStore(paths, null, (boundary, _) => boundary == CustomLoopRunPublicationBoundary.StagedFileFlushed
            ? ValueTask.FromException(new IOException("Injected pre-rename publication failure."))
            : ValueTask.CompletedTask);

        var exception = await Assert.ThrowsAsync<IOException>(() => failing.CreateAsync(run));
        var diagnostic = Assert.IsType<CustomLoopRunPersistenceDiagnostic>(CustomLoopRunPersistenceDiagnostic.Find(exception));
        Assert.Equal(CustomLoopRunPersistenceDiagnosticStage.CanonicalReplace, diagnostic.Stage);
        Assert.DoesNotContain(workspace.RootPath, exception.ToString(), StringComparison.Ordinal);
        using var restarted = new CustomLoopRunStore(paths);
        Assert.Null(await restarted.GetAsync(run.Id));
    }

    private static async Task WriteDirectAsync(WorkspacePaths paths, CustomLoopRunRecord run)
    {
        var content = CustomLoopRunArtifactSerializer.Serialize(run);
        var directory = Path.Combine(paths.CustomLoopRunsPath, run.LoopId);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, run.Id + ".json"), content);
    }

    private static void CreateHardLink(string linkPath, string existingPath)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(CreateWindowsHardLink(linkPath, existingPath, IntPtr.Zero));
            return;
        }

        Assert.Equal(0, CreateUnixHardLink(existingPath, linkPath));
    }

    private static IOException CreateRecognizedTransientIOException()
    {
        var errorCode = OperatingSystem.IsWindows() ? 32 : 11;
        return new IOException("Injected recognized run-evidence contention.", unchecked((int)(0x80070000U | (uint)errorCode)));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateWindowsHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int CreateUnixHardLink(string existingPath, string linkPath);

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
