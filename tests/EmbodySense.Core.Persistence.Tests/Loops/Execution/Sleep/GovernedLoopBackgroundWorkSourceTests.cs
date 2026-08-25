using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Tests.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Persistence.Tests.Triggers.Schedules;
using EmbodySense.Core.Persistence.Triggers.Schedules;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopBackgroundWorkSourceTests
{
    [Fact]
    public async Task Source_returns_detached_bounded_schedule_wake_and_reconciliation_candidates()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var scheduleStore = new ScheduleStore(paths);
        var sleepStore = new GovernedLoopSleepStore(paths);
        var observedAtUtc = GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddHours(2);
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var due = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(runId: "due-run"));
        var future = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(runId: "future-run"),
            observedAtUtc.AddHours(1));
        var reconcile = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(runId: "reconcile-run"));
        await scheduleStore.CreateAsync(ScheduleStoreTestData.CreateRequest());
        await sleepStore.PublishAndReleaseAsync(due, postureHash);
        await sleepStore.PublishAndReleaseAsync(future, postureHash);
        await sleepStore.PublishAndReleaseAsync(reconcile, postureHash);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(
            identity: GovernedLoopSleepContractTestFixture.WakeIdentity(reconcile));
        await sleepStore.CreateWakeAsync(reconcile, prepared, postureHash);

        var source = new GovernedLoopBackgroundWorkSource(scheduleStore, sleepStore);
        var schedules = await source.ReadAsync(GovernedLoopBackgroundWorkFamily.Schedule, observedAtUtc, 16);
        var wakes = await source.ReadAsync(GovernedLoopBackgroundWorkFamily.Wake, observedAtUtc, 16);
        var reconciliations = await source.ReadAsync(GovernedLoopBackgroundWorkFamily.WakeReconciliation, observedAtUtc, 16);

        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, schedules!.ScheduleStatus);
        Assert.Single(schedules.ScheduleCandidates);
        Assert.Equal("daily-reflection", schedules.ScheduleCandidates[0].Value);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, wakes!.WakeStatus);
        var wake = Assert.Single(wakes.WakeCandidates);
        Assert.Equal(due.CheckpointId, wake.CheckpointId);
        Assert.Equal(due.ContentHash, wake.CheckpointHash);
        Assert.Null(wake.AuthenticationEvidenceHash);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, reconciliations!.WakeReconciliationStatus);
        var reconciliation = Assert.Single(reconciliations.WakeReconciliationCandidates);
        Assert.Equal(reconcile.CheckpointId, reconciliation.CheckpointId);
        Assert.Equal(prepared.Identity.WakeId, reconciliation.WakeId);
        Assert.DoesNotContain(wakes.WakeCandidates, item => item.CheckpointId == future.CheckpointId);
    }

    [Fact]
    public async Task Schedule_pages_exclude_future_work_but_include_due_recovery_and_rollback_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ScheduleStore(paths);

        var futureRequest = ScheduleStoreTestData.CreateRequest("future-schedule");
        var futureOccurrence = futureRequest.InitialState.NextOccurrence!;
        var futureState = futureRequest.InitialState with
        {
            LastClockObservedAtUtc = futureOccurrence.ScheduledAtUtc.AddMinutes(-10),
        };
        await store.CreateAsync(futureRequest with { InitialState = futureState });

        var pendingRequest = ScheduleStoreTestData.CreateRequest("pending-schedule", comprehensiveState: true);
        var pendingState = pendingRequest.InitialState with { Enabled = false };
        await store.CreateAsync(pendingRequest with { InitialState = pendingState });

        var rollbackRequest = ScheduleStoreTestData.CreateRequest("rollback-schedule");
        await store.CreateAsync(rollbackRequest);

        var beforeFuture = futureOccurrence.ScheduledAtUtc.AddMinutes(-5);
        var beforeDue = await new GovernedLoopBackgroundWorkSource(store, new GovernedLoopSleepStore(paths))
            .ReadAsync(GovernedLoopBackgroundWorkFamily.Schedule, beforeFuture, 16);
        var atDue = await new GovernedLoopBackgroundWorkSource(store, new GovernedLoopSleepStore(paths))
            .ReadAsync(GovernedLoopBackgroundWorkFamily.Schedule, futureOccurrence.ScheduledAtUtc, 16);

        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, beforeDue!.ScheduleStatus);
        Assert.DoesNotContain(futureRequest.Definition.ScheduleId, beforeDue.ScheduleCandidates);
        Assert.Contains(pendingRequest.Definition.ScheduleId, beforeDue.ScheduleCandidates);
        Assert.Contains(rollbackRequest.Definition.ScheduleId, beforeDue.ScheduleCandidates);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, atDue!.ScheduleStatus);
        Assert.Contains(futureRequest.Definition.ScheduleId, atDue.ScheduleCandidates);
    }

    [Fact]
    public async Task Empty_backpressured_corrupt_and_invalid_queries_have_exact_closed_shapes()
    {
        using var emptyWorkspace = new TestWorkspace();
        var emptyPaths = new WorkspacePaths(emptyWorkspace.RootPath);
        var empty = await new GovernedLoopBackgroundWorkSource(
            new ScheduleStore(emptyPaths),
            new GovernedLoopSleepStore(emptyPaths)).ReadAsync(
                GovernedLoopBackgroundWorkFamily.Schedule,
                GovernedLoopSleepContractTestFixture.PublishedAtUtc,
                1);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Empty, empty!.Status);

        using var boundedWorkspace = new TestWorkspace();
        var boundedPaths = new WorkspacePaths(boundedWorkspace.RootPath);
        var boundedSchedules = new ScheduleStore(boundedPaths);
        await boundedSchedules.CreateAsync(ScheduleStoreTestData.CreateRequest("schedule-a"));
        await boundedSchedules.CreateAsync(ScheduleStoreTestData.CreateRequest("schedule-b"));
        var boundedSource = new GovernedLoopBackgroundWorkSource(boundedSchedules, new GovernedLoopSleepStore(boundedPaths));
        var bounded = await boundedSource.ReadAsync(
            GovernedLoopBackgroundWorkFamily.Schedule,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc,
            1);
        var boundedNext = await boundedSource.ReadAsync(
            GovernedLoopBackgroundWorkFamily.Schedule,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc,
            1);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, bounded!.Status);
        Assert.True(bounded.SchedulePageTruncated);
        Assert.True(boundedNext!.SchedulePageTruncated);
        Assert.Equal(["schedule-a", "schedule-b"], [bounded.ScheduleCandidates[0].Value, boundedNext.ScheduleCandidates[0].Value]);

        using var boundedWakeWorkspace = new TestWorkspace();
        var boundedWakePaths = new WorkspacePaths(boundedWakeWorkspace.RootPath);
        var boundedWakeStore = new GovernedLoopSleepStore(boundedWakePaths);
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        await boundedWakeStore.PublishAndReleaseAsync(
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                GovernedLoopSleepContractTestFixture.Binding(runId: "bounded-wake-a")),
            postureHash);
        await boundedWakeStore.PublishAndReleaseAsync(
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                GovernedLoopSleepContractTestFixture.Binding(runId: "bounded-wake-b")),
            postureHash);
        var boundedWakeSource = new GovernedLoopBackgroundWorkSource(new ScheduleStore(boundedWakePaths), boundedWakeStore);
        var boundedWakes = await boundedWakeSource.ReadAsync(
            GovernedLoopBackgroundWorkFamily.Wake,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddHours(2),
            1);
        var boundedWakesNext = await boundedWakeSource.ReadAsync(
            GovernedLoopBackgroundWorkFamily.Wake,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddHours(2),
            1);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, boundedWakes!.Status);
        Assert.True(boundedWakes.WakePageTruncated);
        Assert.False(boundedWakesNext!.WakePageTruncated);
        Assert.NotEqual(boundedWakes.WakeCandidates[0].CheckpointId, boundedWakesNext.WakeCandidates[0].CheckpointId);

        var byteBoundedWakes = await new GovernedLoopBackgroundWorkSource(
            new ScheduleStore(boundedWakePaths),
            new GovernedLoopSleepStore(boundedWakePaths, new GovernedLoopSleepStoreOptions { MaxCatalogUtf8Bytes = 128 }))
            .ReadAsync(
                GovernedLoopBackgroundWorkFamily.Wake,
                GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddHours(2),
                2);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Corrupt, byteBoundedWakes!.Status);

        using var corruptWorkspace = new TestWorkspace();
        var corruptPaths = new WorkspacePaths(corruptWorkspace.RootPath);
        var corruptSchedules = new ScheduleStore(corruptPaths);
        await corruptSchedules.CreateAsync(ScheduleStoreTestData.CreateRequest());
        var ledger = Directory.EnumerateFiles(
            corruptPaths.AgentFile(Path.Combine("triggers", "schedules")),
            "ledger-*.json").Single();
        var root = JsonNode.Parse(await File.ReadAllBytesAsync(ledger))!.AsObject();
        root["schemaVersion"] = 2;
        await File.WriteAllTextAsync(ledger, root.ToJsonString());
        var corrupt = await new GovernedLoopBackgroundWorkSource(corruptSchedules, new GovernedLoopSleepStore(corruptPaths))
            .ReadAsync(
                GovernedLoopBackgroundWorkFamily.Schedule,
                GovernedLoopSleepContractTestFixture.PublishedAtUtc,
                1);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Corrupt, corrupt!.Status);
        Assert.Empty(corrupt.ScheduleCandidates);

        using var corruptSleepWorkspace = new TestWorkspace();
        var corruptSleepPaths = new WorkspacePaths(corruptSleepWorkspace.RootPath);
        var corruptSleepStore = new GovernedLoopSleepStore(corruptSleepPaths);
        await corruptSleepStore.PublishAndReleaseAsync(
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(),
            postureHash);
        var sleepLedger = Directory.EnumerateFiles(
            corruptSleepPaths.AgentFile(Path.Combine("loops", "execution", "sleep")),
            "ledger-*.json").Single();
        var sleepRoot = JsonNode.Parse(await File.ReadAllBytesAsync(sleepLedger))!.AsObject();
        sleepRoot["schemaVersion"] = 2;
        await File.WriteAllTextAsync(sleepLedger, sleepRoot.ToJsonString());
        var corruptSleep = await new GovernedLoopBackgroundWorkSource(new ScheduleStore(corruptSleepPaths), corruptSleepStore)
            .ReadAsync(
                GovernedLoopBackgroundWorkFamily.Wake,
                GovernedLoopSleepContractTestFixture.PublishedAtUtc,
                1);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Corrupt, corruptSleep!.Status);

        var invalid = await new GovernedLoopBackgroundWorkSource(
            new ScheduleStore(emptyPaths),
            new GovernedLoopSleepStore(emptyPaths)).ReadAsync(
                GovernedLoopBackgroundWorkFamily.Schedule,
                DateTimeOffset.Now,
                0);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Corrupt, invalid!.Status);
        Assert.True(EmbodySense.Core.Application.Loops.Sleep.GovernedLoopBackgroundWorkContract.IsValid(invalid, 1));
    }

    [Fact]
    public async Task Family_specific_pages_preserve_overload_posture_without_suppressing_work()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var schedules = new ScheduleStore(paths);
        var sleep = new GovernedLoopSleepStore(paths);
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var observedAtUtc = GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddHours(2);
        await schedules.CreateAsync(ScheduleStoreTestData.CreateRequest("schedule-a"));
        await schedules.CreateAsync(ScheduleStoreTestData.CreateRequest("schedule-b"));
        var dueA = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(runId: "due-a"));
        var dueB = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(runId: "due-b"));
        var reconcile = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(runId: "reconcile"));
        await sleep.PublishAndReleaseAsync(dueA, postureHash);
        await sleep.PublishAndReleaseAsync(dueB, postureHash);
        await sleep.PublishAndReleaseAsync(reconcile, postureHash);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(
            identity: GovernedLoopSleepContractTestFixture.WakeIdentity(reconcile));
        await sleep.CreateWakeAsync(reconcile, prepared, postureHash);

        var source = new GovernedLoopBackgroundWorkSource(schedules, sleep);
        var schedulePage = await source.ReadAsync(GovernedLoopBackgroundWorkFamily.Schedule, observedAtUtc, 1);
        var wakePage = await source.ReadAsync(GovernedLoopBackgroundWorkFamily.Wake, observedAtUtc, 1);
        var reconciliationPage = await source.ReadAsync(GovernedLoopBackgroundWorkFamily.WakeReconciliation, observedAtUtc, 1);

        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, schedulePage!.ScheduleStatus);
        Assert.True(schedulePage.SchedulePageTruncated);
        Assert.Single(schedulePage.ScheduleCandidates);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, wakePage!.WakeStatus);
        Assert.True(wakePage.WakePageTruncated);
        Assert.Single(wakePage.WakeCandidates);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, reconciliationPage!.WakeReconciliationStatus);
        Assert.False(reconciliationPage.WakeReconciliationPageTruncated);
        Assert.Equal(prepared.Identity.WakeId, Assert.Single(reconciliationPage.WakeReconciliationCandidates).WakeId);
    }

    [Fact]
    public async Task Sleep_pages_use_monotonic_keysets_for_exact_tail_stale_anchors_and_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var sleep = new GovernedLoopSleepStore(paths);
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var observedAtUtc = GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddHours(2);
        var wakes = new[]
        {
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                GovernedLoopSleepContractTestFixture.Binding(runId: "cursor-wake-a")),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                GovernedLoopSleepContractTestFixture.Binding(runId: "cursor-wake-b")),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                GovernedLoopSleepContractTestFixture.Binding(runId: "cursor-wake-c")),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                GovernedLoopSleepContractTestFixture.Binding(runId: "cursor-wake-d"))
        };
        var reconciliations = new[]
        {
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                GovernedLoopSleepContractTestFixture.Binding(runId: "cursor-reconciliation-a")),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                GovernedLoopSleepContractTestFixture.Binding(runId: "cursor-reconciliation-b")),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                GovernedLoopSleepContractTestFixture.Binding(runId: "cursor-reconciliation-c")),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                GovernedLoopSleepContractTestFixture.Binding(runId: "cursor-reconciliation-d"))
        };
        var reconciliationEvidence = new List<(GovernedLoopSleepCheckpoint Checkpoint, GovernedLoopWakeEvidence Evidence)>();
        foreach (var checkpoint in wakes)
        {
            Assert.Equal(
                GovernedLoopSleepCheckpointMutationStatus.Committed,
                (await sleep.PublishAndReleaseAsync(checkpoint, postureHash))!.Status);
        }

        foreach (var checkpoint in reconciliations)
        {
            Assert.Equal(
                GovernedLoopSleepCheckpointMutationStatus.Committed,
                (await sleep.PublishAndReleaseAsync(checkpoint, postureHash))!.Status);
            var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(
                identity: GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint));
            Assert.Equal(
                GovernedLoopWakeEvidenceMutationStatus.Committed,
                (await sleep.CreateWakeAsync(checkpoint, prepared, postureHash))!.Status);
            reconciliationEvidence.Add((checkpoint, prepared));
        }

        var orderedWakes = wakes.OrderBy(item => item.CheckpointId, StringComparer.Ordinal).ToArray();
        var orderedReconciliations = reconciliations.OrderBy(item => item.CheckpointId, StringComparer.Ordinal).ToArray();

        async Task AssertCursorContractAsync(
            GovernedLoopBackgroundWorkFamily family,
            IReadOnlyList<GovernedLoopSleepCheckpoint> ordered)
        {
            var source = new GovernedLoopBackgroundWorkSource(new ScheduleStore(paths), sleep);
            var first = await source.ReadAsync(family, observedAtUtc, 1);
            Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, FamilyStatus(first!, family));
            Assert.Equal(ordered[0].CheckpointId, FamilyIds(first!, family).Single());
            Assert.True(FamilyTruncated(first!, family));

            var middle = await source.ReadAsync(family, observedAtUtc, 1);
            Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, FamilyStatus(middle!, family));
            Assert.Equal(ordered[1].CheckpointId, FamilyIds(middle!, family).Single());
            Assert.True(FamilyTruncated(middle!, family));

            var last = await source.ReadAsync(family, observedAtUtc, 2);
            Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, FamilyStatus(last!, family));
            Assert.Equal(ordered.Skip(2).Select(item => item.CheckpointId), FamilyIds(last!, family));
            Assert.False(FamilyTruncated(last!, family));

            var afterTail = await source.ReadAsync(family, observedAtUtc, 1);
            Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Empty, FamilyStatus(afterTail!, family));
            Assert.Empty(FamilyIds(afterTail!, family));
            Assert.False(FamilyTruncated(afterTail!, family));

            var restarted = new GovernedLoopBackgroundWorkSource(new ScheduleStore(paths), sleep);
            var restartPage = await restarted.ReadAsync(family, observedAtUtc, 2);
            Assert.Equal(ordered.Take(2).Select(item => item.CheckpointId), FamilyIds(restartPage!, family));
            Assert.True(FamilyTruncated(restartPage!, family));
        }

        await AssertCursorContractAsync(
            GovernedLoopBackgroundWorkFamily.Wake,
            orderedWakes);
        await AssertCursorContractAsync(
            GovernedLoopBackgroundWorkFamily.WakeReconciliation,
            orderedReconciliations);

        var staleReconciliationSource = new GovernedLoopBackgroundWorkSource(new ScheduleStore(paths), sleep);
        var staleReconciliationPrefix = await staleReconciliationSource.ReadAsync(
            GovernedLoopBackgroundWorkFamily.WakeReconciliation,
            observedAtUtc,
            4);
        Assert.Equal(
            orderedReconciliations.Select(item => item.CheckpointId),
            staleReconciliationPrefix!.WakeReconciliationCandidates.Select(item => item.CheckpointId));
        Assert.False(staleReconciliationPrefix.WakeReconciliationPageTruncated);
        var staleReconciliation = orderedReconciliations[^1];
        var staleReconciliationEvidence = reconciliationEvidence.Single(
            item => item.Checkpoint.CheckpointId == staleReconciliation.CheckpointId).Evidence;
        var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            evidenceVersion: staleReconciliationEvidence.EvidenceVersion + 1,
            identity: staleReconciliationEvidence.Identity);
        Assert.Equal(
            GovernedLoopWakeEvidenceMutationStatus.Committed,
            (await sleep.AdvanceWakeAsync(staleReconciliationEvidence, committed))!.Status);
        var staleReconciliationPage = await staleReconciliationSource.ReadAsync(
            GovernedLoopBackgroundWorkFamily.WakeReconciliation,
            observedAtUtc,
            1);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Empty, staleReconciliationPage!.WakeReconciliationStatus);
        Assert.Empty(staleReconciliationPage.WakeReconciliationCandidates);
        Assert.False(staleReconciliationPage.WakeReconciliationPageTruncated);

        var staleWake = orderedWakes[^1];
        var staleWakeSource = new GovernedLoopBackgroundWorkSource(new ScheduleStore(paths), sleep);
        var staleWakePrefix = await staleWakeSource.ReadAsync(GovernedLoopBackgroundWorkFamily.Wake, observedAtUtc, 4);
        Assert.Equal(orderedWakes.Select(item => item.CheckpointId), staleWakePrefix!.WakeCandidates.Select(item => item.CheckpointId));
        Assert.False(staleWakePrefix.WakePageTruncated);
        var staleWakeEvidence = GovernedLoopSleepContractTestFixture.WakeEvidence(
            identity: GovernedLoopSleepContractTestFixture.WakeIdentity(staleWake));
        Assert.Equal(
            GovernedLoopWakeEvidenceMutationStatus.Committed,
            (await sleep.CreateWakeAsync(staleWake, staleWakeEvidence, postureHash))!.Status);
        var staleWakePage = await staleWakeSource.ReadAsync(GovernedLoopBackgroundWorkFamily.Wake, observedAtUtc, 1);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Empty, staleWakePage!.WakeStatus);
        Assert.Empty(staleWakePage.WakeCandidates);
        Assert.False(staleWakePage.WakePageTruncated);
    }

    [Fact]
    public async Task Sleep_source_rescans_after_one_tail_boundary_without_repeating_completed_work()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var sleep = new GovernedLoopSleepStore(paths);
        var schedules = new ScheduleStore(paths);
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var observedAtUtc = GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddHours(2);
        var wakes = Enumerable.Range(0, 3)
            .Select(index => GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                GovernedLoopSleepContractTestFixture.Binding(runId: $"rescan-wake-{index}")))
            .ToArray();
        var reconciliations = Enumerable.Range(0, 3)
            .Select(index => GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                GovernedLoopSleepContractTestFixture.Binding(runId: $"rescan-reconciliation-{index}")))
            .ToArray();
        var reconciliationEvidence = new Dictionary<string, GovernedLoopWakeEvidence>(StringComparer.Ordinal);
        foreach (var checkpoint in wakes)
        {
            await sleep.PublishAndReleaseAsync(checkpoint, postureHash);
        }

        foreach (var checkpoint in reconciliations)
        {
            await sleep.PublishAndReleaseAsync(checkpoint, postureHash);
            var evidence = GovernedLoopSleepContractTestFixture.WakeEvidence(
                identity: GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint));
            await sleep.CreateWakeAsync(checkpoint, evidence, postureHash);
            reconciliationEvidence.Add(checkpoint.CheckpointId, evidence);
        }

        var source = new GovernedLoopBackgroundWorkSource(schedules, sleep);

        async Task AssertRescanAsync(
            GovernedLoopBackgroundWorkFamily family,
            IReadOnlyList<GovernedLoopSleepCheckpoint> initialCandidates,
            Func<GovernedLoopSleepCheckpoint, Task> complete,
            Func<GovernedLoopSleepCheckpoint, Task> publish)
        {
            var ordered = initialCandidates.OrderBy(item => item.CheckpointId, StringComparer.Ordinal).ToArray();
            var first = await source.ReadAsync(family, observedAtUtc, 2);
            var final = await source.ReadAsync(family, observedAtUtc, 2);
            Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, FamilyStatus(first!, family));
            Assert.True(FamilyTruncated(first!, family));
            Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, FamilyStatus(final!, family));
            Assert.False(FamilyTruncated(final!, family));
            Assert.Equal(
                ordered.Select(item => item.CheckpointId),
                FamilyIds(first!, family).Concat(FamilyIds(final!, family)));
            Assert.Equal(
                ordered.Length,
                FamilyIds(first!, family).Concat(FamilyIds(final!, family)).Distinct(StringComparer.Ordinal).Count());

            var boundary = await source.ReadAsync(family, observedAtUtc, 2);
            Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Empty, FamilyStatus(boundary!, family));
            Assert.Empty(FamilyIds(boundary!, family));
            Assert.False(FamilyTruncated(boundary!, family));

            await complete(ordered[0]);
            var newlyEligible = Enumerable.Range(0, 128)
                .Select(index => GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                    GovernedLoopSleepContractTestFixture.Binding(runId: $"rescan-new-{family.ToString().ToLowerInvariant()}-{index}")))
                .First(candidate => StringComparer.Ordinal.Compare(candidate.CheckpointId, ordered[^1].CheckpointId) <= 0);
            Assert.True(StringComparer.Ordinal.Compare(newlyEligible.CheckpointId, ordered[^1].CheckpointId) <= 0);
            await publish(newlyEligible);

            var rescan = await source.ReadAsync(family, observedAtUtc, 16);
            var expected = ordered.Skip(1)
                .Append(newlyEligible)
                .OrderBy(item => item.CheckpointId, StringComparer.Ordinal)
                .Select(item => item.CheckpointId)
                .ToArray();
            Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, FamilyStatus(rescan!, family));
            Assert.False(FamilyTruncated(rescan!, family));
            Assert.Equal(expected, FamilyIds(rescan!, family));
            Assert.DoesNotContain(ordered[0].CheckpointId, FamilyIds(rescan!, family));
        }

        await AssertRescanAsync(
            GovernedLoopBackgroundWorkFamily.WakeReconciliation,
            reconciliations,
            async checkpoint =>
            {
                var evidence = reconciliationEvidence[checkpoint.CheckpointId];
                var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
                    GovernedLoopWakeDisposition.Committed,
                    evidenceVersion: evidence.EvidenceVersion + 1,
                    identity: evidence.Identity);
                Assert.Equal(
                    GovernedLoopWakeEvidenceMutationStatus.Committed,
                    (await sleep.AdvanceWakeAsync(evidence, committed))!.Status);
            },
            async checkpoint =>
            {
                Assert.Equal(
                    GovernedLoopSleepCheckpointMutationStatus.Committed,
                    (await sleep.PublishAndReleaseAsync(checkpoint, postureHash))!.Status);
                var evidence = GovernedLoopSleepContractTestFixture.WakeEvidence(
                    identity: GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint));
                Assert.Equal(
                    GovernedLoopWakeEvidenceMutationStatus.Committed,
                    (await sleep.CreateWakeAsync(checkpoint, evidence, postureHash))!.Status);
            });

        await AssertRescanAsync(
            GovernedLoopBackgroundWorkFamily.Wake,
            wakes,
            async checkpoint =>
            {
                var evidence = GovernedLoopSleepContractTestFixture.WakeEvidence(
                    identity: GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint));
                Assert.Equal(
                    GovernedLoopWakeEvidenceMutationStatus.Committed,
                    (await sleep.CreateWakeAsync(checkpoint, evidence, postureHash))!.Status);
            },
            async checkpoint =>
            {
                Assert.Equal(
                    GovernedLoopSleepCheckpointMutationStatus.Committed,
                    (await sleep.PublishAndReleaseAsync(checkpoint, postureHash))!.Status);
            });
    }

    [Fact]
    public async Task Page_limit_two_advances_schedule_by_wrapping_but_sleep_by_monotonic_keyset()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var schedules = new ScheduleStore(paths);
        var sleep = new GovernedLoopSleepStore(paths);
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var observedAtUtc = GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddHours(2);
        foreach (var suffix in new[] { "d", "b", "a", "c" })
        {
            await schedules.CreateAsync(ScheduleStoreTestData.CreateRequest($"schedule-{suffix}"));
        }

        var wakes = new List<GovernedLoopSleepCheckpoint>();
        var reconciliations = new List<(GovernedLoopSleepCheckpoint Checkpoint, GovernedLoopWakeEvidence Evidence)>();
        foreach (var suffix in new[] { "d", "b", "a", "c" })
        {
            var wake = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                GovernedLoopSleepContractTestFixture.Binding(runId: $"wake-{suffix}"));
            await sleep.PublishAndReleaseAsync(wake, postureHash);
            wakes.Add(wake);
            var reconciliation = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
                GovernedLoopSleepContractTestFixture.Binding(runId: $"reconciliation-{suffix}"));
            await sleep.PublishAndReleaseAsync(reconciliation, postureHash);
            var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(
                identity: GovernedLoopSleepContractTestFixture.WakeIdentity(reconciliation));
            await sleep.CreateWakeAsync(reconciliation, prepared, postureHash);
            reconciliations.Add((reconciliation, prepared));
        }

        var source = new GovernedLoopBackgroundWorkSource(schedules, sleep);
        var schedulePages = new[]
        {
            await source.ReadAsync(GovernedLoopBackgroundWorkFamily.Schedule, observedAtUtc, 2),
            await source.ReadAsync(GovernedLoopBackgroundWorkFamily.Schedule, observedAtUtc, 2),
            await source.ReadAsync(GovernedLoopBackgroundWorkFamily.Schedule, observedAtUtc, 2)
        };
        var wakePages = new[]
        {
            await source.ReadAsync(GovernedLoopBackgroundWorkFamily.Wake, observedAtUtc, 2),
            await source.ReadAsync(GovernedLoopBackgroundWorkFamily.Wake, observedAtUtc, 2),
            await source.ReadAsync(GovernedLoopBackgroundWorkFamily.Wake, observedAtUtc, 2)
        };
        var reconciliationPages = new[]
        {
            await source.ReadAsync(GovernedLoopBackgroundWorkFamily.WakeReconciliation, observedAtUtc, 2),
            await source.ReadAsync(GovernedLoopBackgroundWorkFamily.WakeReconciliation, observedAtUtc, 2),
            await source.ReadAsync(GovernedLoopBackgroundWorkFamily.WakeReconciliation, observedAtUtc, 2)
        };

        Assert.Equal(
            ["schedule-a", "schedule-b", "schedule-c", "schedule-d", "schedule-a", "schedule-b"],
            schedulePages.SelectMany(page => page!.ScheduleCandidates.Select(item => item.Value)).ToArray());
        var orderedWakeIds = wakes.Select(item => item.CheckpointId).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            orderedWakeIds,
            wakePages.SelectMany(page => page!.WakeCandidates.Select(item => item.CheckpointId)).ToArray());
        var orderedReconciliationIds = reconciliations
            .Select(item => item.Checkpoint.CheckpointId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            orderedReconciliationIds,
            reconciliationPages.SelectMany(page => page!.WakeReconciliationCandidates.Select(item => item.CheckpointId)).ToArray());
        Assert.All(schedulePages, page => Assert.True(page!.SchedulePageTruncated));
        Assert.True(wakePages[0]!.WakePageTruncated);
        Assert.False(wakePages[1]!.WakePageTruncated);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Empty, wakePages[2]!.WakeStatus);
        Assert.False(wakePages[2]!.WakePageTruncated);
        Assert.True(reconciliationPages[0]!.WakeReconciliationPageTruncated);
        Assert.False(reconciliationPages[1]!.WakeReconciliationPageTruncated);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Empty, reconciliationPages[2]!.WakeReconciliationStatus);
        Assert.False(reconciliationPages[2]!.WakeReconciliationPageTruncated);

        var restarted = await new GovernedLoopBackgroundWorkSource(schedules, sleep)
            .ReadAsync(GovernedLoopBackgroundWorkFamily.Wake, observedAtUtc, 2);
        Assert.Equal(orderedWakeIds[..2], restarted!.WakeCandidates.Select(item => item.CheckpointId).ToArray());
    }

    private static GovernedLoopBackgroundWorkReadStatus FamilyStatus(
        GovernedLoopBackgroundWorkReadResult result,
        GovernedLoopBackgroundWorkFamily family)
        => family switch
        {
            GovernedLoopBackgroundWorkFamily.Wake => result.WakeStatus,
            GovernedLoopBackgroundWorkFamily.WakeReconciliation => result.WakeReconciliationStatus,
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, null)
        };

    private static IReadOnlyList<string> FamilyIds(
        GovernedLoopBackgroundWorkReadResult result,
        GovernedLoopBackgroundWorkFamily family)
        => family switch
        {
            GovernedLoopBackgroundWorkFamily.Wake => result.WakeCandidates.Select(item => item.CheckpointId).ToArray(),
            GovernedLoopBackgroundWorkFamily.WakeReconciliation => result.WakeReconciliationCandidates.Select(item => item.CheckpointId).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, null)
        };

    private static bool FamilyTruncated(
        GovernedLoopBackgroundWorkReadResult result,
        GovernedLoopBackgroundWorkFamily family)
        => family switch
        {
            GovernedLoopBackgroundWorkFamily.Wake => result.WakePageTruncated,
            GovernedLoopBackgroundWorkFamily.WakeReconciliation => result.WakeReconciliationPageTruncated,
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, null)
        };

}
