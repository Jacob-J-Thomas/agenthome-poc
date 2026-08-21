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
        Assert.True(boundedWakesNext!.WakePageTruncated);
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
    public async Task Page_limit_two_advances_every_family_in_stable_order_before_deterministic_wrap()
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
            [.. orderedWakeIds, .. orderedWakeIds[..2]],
            wakePages.SelectMany(page => page!.WakeCandidates.Select(item => item.CheckpointId)).ToArray());
        var orderedReconciliationIds = reconciliations
            .Select(item => item.Checkpoint.CheckpointId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [.. orderedReconciliationIds, .. orderedReconciliationIds[..2]],
            reconciliationPages.SelectMany(page => page!.WakeReconciliationCandidates.Select(item => item.CheckpointId)).ToArray());
        Assert.All(schedulePages, page => Assert.True(page!.SchedulePageTruncated));
        Assert.All(wakePages, page => Assert.True(page!.WakePageTruncated));
        Assert.All(reconciliationPages, page => Assert.True(page!.WakeReconciliationPageTruncated));
    }
}
