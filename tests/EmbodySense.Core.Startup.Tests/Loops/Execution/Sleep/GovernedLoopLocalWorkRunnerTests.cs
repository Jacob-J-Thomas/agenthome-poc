using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Tests.Triggers;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopLocalWorkRunnerTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 11, 21, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ScheduleEvaluationStatus.NotFound, GovernedLoopLocalWorkResultStatus.Empty)]
    [InlineData(ScheduleEvaluationStatus.NotDue, GovernedLoopLocalWorkResultStatus.Empty)]
    [InlineData(ScheduleEvaluationStatus.Disabled, GovernedLoopLocalWorkResultStatus.Empty)]
    [InlineData(ScheduleEvaluationStatus.Exhausted, GovernedLoopLocalWorkResultStatus.Empty)]
    [InlineData(ScheduleEvaluationStatus.Skipped, GovernedLoopLocalWorkResultStatus.Completed)]
    [InlineData(ScheduleEvaluationStatus.Deferred, GovernedLoopLocalWorkResultStatus.Completed)]
    [InlineData(ScheduleEvaluationStatus.Queued, GovernedLoopLocalWorkResultStatus.Completed)]
    [InlineData(ScheduleEvaluationStatus.Replayed, GovernedLoopLocalWorkResultStatus.Completed)]
    [InlineData(ScheduleEvaluationStatus.Rejected, GovernedLoopLocalWorkResultStatus.Completed)]
    [InlineData(ScheduleEvaluationStatus.Backpressured, GovernedLoopLocalWorkResultStatus.Backpressured)]
    [InlineData(ScheduleEvaluationStatus.PermissionDenied, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    [InlineData(ScheduleEvaluationStatus.Conflict, GovernedLoopLocalWorkResultStatus.Conflict)]
    [InlineData(ScheduleEvaluationStatus.Unavailable, GovernedLoopLocalWorkResultStatus.Unavailable)]
    [InlineData(ScheduleEvaluationStatus.Corrupt, GovernedLoopLocalWorkResultStatus.Corrupt)]
    [InlineData(ScheduleEvaluationStatus.ClockRollback, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    [InlineData(ScheduleEvaluationStatus.NeedsReview, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    [InlineData(ScheduleEvaluationStatus.BoundExceeded, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    [InlineData(ScheduleEvaluationStatus.Unknown, GovernedLoopLocalWorkResultStatus.Corrupt)]
    public async Task Schedule_candidates_use_the_canonical_one_shot_and_closed_status_mapping(
        ScheduleEvaluationStatus evaluationStatus,
        GovernedLoopLocalWorkResultStatus expectedStatus)
    {
        Assert.True(ScheduleId.TryParse("schedule-1", out var scheduleId));
        var source = Source(
            GovernedLoopBackgroundWorkReadStatus.Found,
            [scheduleId!]);
        ScheduleId? observedSchedule = null;
        var runner = Runner(
            source,
            evaluateSchedule: (candidate, _) =>
            {
                observedSchedule = candidate;
                return Task.FromResult(ScheduleResult(evaluationStatus, candidate));
            });

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule);

        Assert.Equal(expectedStatus, result!.Status);
        Assert.Equal(scheduleId, observedSchedule);
        Assert.Equal(_now, source.LastObservedAtUtc);
        Assert.Equal(4, source.LastPerFamilyMax);
    }

    [Fact]
    public async Task Schedule_rotation_prevents_a_non_actionable_first_candidate_from_starving_a_due_candidate()
    {
        Assert.True(ScheduleId.TryParse("schedule-1", out var firstId));
        Assert.True(ScheduleId.TryParse("schedule-2", out var secondId));
        var source = Source(
            GovernedLoopBackgroundWorkReadStatus.Found,
            [firstId!, secondId!]);
        var observed = new List<ScheduleId>();
        var runner = Runner(
            source,
            evaluateSchedule: (candidate, _) =>
            {
                observed.Add(candidate);
                var status = candidate.Equals(firstId)
                    ? ScheduleEvaluationStatus.NotDue
                    : ScheduleEvaluationStatus.Queued;
                return Task.FromResult(ScheduleResult(status, candidate));
            });

        var first = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule);
        var second = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, first!.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, second!.Status);
        Assert.Equal([firstId!, secondId!], observed);
    }

    [Fact]
    public async Task Schedule_rotation_remains_bounded_when_the_candidate_list_changes_size()
    {
        Assert.True(ScheduleId.TryParse("schedule-1", out var firstId));
        Assert.True(ScheduleId.TryParse("schedule-2", out var secondId));
        Assert.True(ScheduleId.TryParse("schedule-3", out var replacementId));
        IReadOnlyList<ScheduleId> candidates = [firstId!, secondId!];
        var source = new ScriptedBackgroundWorkSource
        {
            Handler = (_, _, _, _) => Task.FromResult<GovernedLoopBackgroundWorkReadResult?>(
                GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
                    GovernedLoopBackgroundWorkReadStatus.Found,
                    candidates,
                    [],
                    []))
        };
        var observed = new List<ScheduleId>();
        var runner = Runner(
            source,
            evaluateSchedule: (candidate, _) =>
            {
                observed.Add(candidate);
                return Task.FromResult(ScheduleResult(ScheduleEvaluationStatus.Queued, candidate));
            });

        await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule);
        candidates = [replacementId!];
        await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule);
        await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule);

        Assert.Equal([firstId!, secondId!, replacementId!], observed);
    }

    [Theory]
    [InlineData(GovernedLoopBackgroundWorkReadStatus.Empty, GovernedLoopLocalWorkResultStatus.Empty)]
    [InlineData(GovernedLoopBackgroundWorkReadStatus.Backpressured, GovernedLoopLocalWorkResultStatus.Backpressured)]
    [InlineData(GovernedLoopBackgroundWorkReadStatus.Corrupt, GovernedLoopLocalWorkResultStatus.Corrupt)]
    [InlineData(GovernedLoopBackgroundWorkReadStatus.Unavailable, GovernedLoopLocalWorkResultStatus.Unavailable)]
    public async Task Background_discovery_statuses_are_projected_without_invoking_a_subsystem(
        GovernedLoopBackgroundWorkReadStatus readStatus,
        GovernedLoopLocalWorkResultStatus expectedStatus)
    {
        var source = Source(readStatus);
        var scheduleCalls = 0;
        var runner = Runner(
            source,
            evaluateSchedule: (candidate, _) =>
            {
                scheduleCalls++;
                return Task.FromResult(ScheduleResult(ScheduleEvaluationStatus.Queued, candidate));
            });

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule);

        Assert.Equal(expectedStatus, result!.Status);
        Assert.Equal(0, scheduleCalls);
    }

    [Fact]
    public async Task Malformed_and_throwing_background_sources_fail_closed()
    {
        var malformed = new ScriptedBackgroundWorkSource
        {
            Handler = static (_, _, _, _) => Task.FromResult<GovernedLoopBackgroundWorkReadResult?>(null)
        };
        var unavailable = new ScriptedBackgroundWorkSource
        {
            Handler = static (_, _, _, _) => throw new IOException("hostile source")
        };

        var malformedResult = await Runner(malformed).RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule);
        var unavailableResult = await Runner(unavailable).RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, malformedResult!.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Unavailable, unavailableResult!.Status);
    }

    [Fact]
    public async Task Healthy_reconciliation_runs_when_schedule_and_ordinary_wake_families_are_backpressured()
    {
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var request = new GovernedLoopWakeReconciliationRequest(hashA, hashB);
        var source = new ScriptedBackgroundWorkSource
        {
            Handler = (_, _, _, _) => Task.FromResult<GovernedLoopBackgroundWorkReadResult?>(
                GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
                    GovernedLoopBackgroundWorkReadStatus.Backpressured,
                    [],
                    GovernedLoopBackgroundWorkReadStatus.Backpressured,
                    [],
                    GovernedLoopBackgroundWorkReadStatus.Found,
                    [request]))
        };
        GovernedLoopWakeReconciliationRequest? observed = null;
        var runner = Runner(
            source,
            reconcileWake: (candidate, _) =>
            {
                observed = candidate;
                return Task.FromResult(new GovernedLoopWakeResult(
                    GovernedLoopWakeResultStatus.Committed,
                    WakeEvidence(GovernedLoopWakeDisposition.Committed),
                    ContinuationInvoked: true));
            });

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, result!.Status);
        Assert.Equal(request, observed);
    }

    [Fact]
    public async Task Malformed_schedule_and_wake_service_results_fail_closed()
    {
        Assert.True(ScheduleId.TryParse("schedule-1", out var scheduleId));
        var schedule = Runner(
            Source(GovernedLoopBackgroundWorkReadStatus.Found, [scheduleId!]),
            evaluateSchedule: static (_, _) => Task.FromResult(
                new ScheduleEvaluationResult(ScheduleEvaluationStatus.Queued, string.Empty, null)));
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var wake = Runner(
            Source(
                GovernedLoopBackgroundWorkReadStatus.Found,
                wakes: [new GovernedLoopWakeRequest(hashA, hashB)]),
            wake: static (_, _) => Task.FromResult(
                new GovernedLoopWakeResult(
                    GovernedLoopWakeResultStatus.Committed,
                    Evidence: null,
                    ContinuationInvoked: true)));
        var mismatchedWake = Runner(
            Source(
                GovernedLoopBackgroundWorkReadStatus.Found,
                wakes: [new GovernedLoopWakeRequest(hashA, hashB)]),
            wake: (_, _) => Task.FromResult(
                new GovernedLoopWakeResult(
                    GovernedLoopWakeResultStatus.Committed,
                    WakeEvidence(GovernedLoopWakeDisposition.Paused))));

        var scheduleResult = await schedule.RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule);
        var wakeResult = await wake.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);
        var mismatchedWakeResult = await mismatchedWake.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, scheduleResult!.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, wakeResult!.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, mismatchedWakeResult!.Status);
    }

    [Fact]
    public async Task Throwing_one_shot_dependencies_are_bounded_as_unavailable()
    {
        Assert.True(ScheduleId.TryParse("schedule-1", out var scheduleId));
        var schedule = Runner(
            Source(GovernedLoopBackgroundWorkReadStatus.Found, [scheduleId!]),
            evaluateSchedule: static (_, _) => throw new IOException("hostile schedule"));
        var triggerQuery = Runner(
            readTriggerQueue: static (_, _) => throw new IOException("hostile query"));
        var triggerWorker = Runner(
            readTriggerQueue: (_, _) => Task.FromResult(Snapshot()),
            runTrigger: static (_, _) => throw new IOException("hostile worker"));
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var wake = Runner(
            Source(
                GovernedLoopBackgroundWorkReadStatus.Found,
                wakes: [new GovernedLoopWakeRequest(hashA, hashB)]),
            wake: static (_, _) => throw new IOException("hostile wake"));

        Assert.Equal(
            GovernedLoopLocalWorkResultStatus.Unavailable,
            (await schedule.RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule))!.Status);
        Assert.Equal(
            GovernedLoopLocalWorkResultStatus.Unavailable,
            (await triggerQuery.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger))!.Status);
        Assert.Equal(
            GovernedLoopLocalWorkResultStatus.Unavailable,
            (await triggerWorker.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger))!.Status);
        Assert.Equal(
            GovernedLoopLocalWorkResultStatus.Unavailable,
            (await wake.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake))!.Status);
    }

    [Fact]
    public async Task Trigger_selection_uses_exact_queue_generation_and_bounded_newest_last_fairness_history()
    {
        var observed = new List<TriggerWorkerSelectionRequest>();
        var runNumber = 0;
        var runner = Runner(
            readTriggerQueue: (_, _) => Task.FromResult(Snapshot(generation: 7)),
            runTrigger: (request, _) =>
            {
                observed.Add(request.Selection);
                runNumber++;
                return Task.FromResult(new TriggerWorkerRunResult(
                    TriggerWorkerSelectionStatus.Acquired,
                    TriggerWorkerMutationStatus.Committed,
                    Entry($"loop-{runNumber}")));
            });

        var first = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger);
        var second = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, first!.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, second!.Status);
        Assert.Equal(2, observed.Count);
        Assert.Equal("local-worker", observed[0].WorkerId);
        Assert.Equal(7, observed[0].ExpectedQueueGeneration);
        Assert.Equal(_now, observed[0].ObservedAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(1), observed[0].LeaseDuration);
        Assert.Empty(observed[0].RecentLoopIds);
        Assert.Equal(["loop-1"], observed[1].RecentLoopIds);
        Assert.Equal(2, observed[1].MaxConsecutiveSelectionsPerLoop);
    }

    [Fact]
    public async Task Retained_schedule_reselection_precedes_the_ordinary_trigger_queue()
    {
        var queueReads = 0;
        var workerCalls = 0;
        var runner = Runner(
            retryScheduleAdmission: (_, _) => Task.FromResult(
                new GovernedLoopLocalWorkResult(
                    GovernedLoopLocalWorkResultStatus.Completed,
                    "schedule-retry-materialized")),
            readTriggerQueue: (_, _) =>
            {
                queueReads++;
                return Task.FromResult(Snapshot());
            },
            runTrigger: (_, _) =>
            {
                workerCalls++;
                return Task.FromResult(new TriggerWorkerRunResult(TriggerWorkerSelectionStatus.Empty, null, null));
            });

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, result!.Status);
        Assert.Equal("schedule-retry-materialized", result.ReasonCode);
        Assert.Equal(0, queueReads);
        Assert.Equal(0, workerCalls);
    }

    [Fact]
    public async Task Empty_retained_schedule_reselection_continues_to_the_ordinary_trigger_queue()
    {
        var retryReads = 0;
        var queueReads = 0;
        var runner = Runner(
            retryScheduleAdmission: (observedAtUtc, _) =>
            {
                retryReads++;
                Assert.Equal(_now, observedAtUtc);
                return Task.FromResult(new GovernedLoopLocalWorkResult(
                    GovernedLoopLocalWorkResultStatus.Empty,
                    "schedule-retry-empty"));
            },
            readTriggerQueue: (_, _) =>
            {
                queueReads++;
                return Task.FromResult(Snapshot());
            });

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, result!.Status);
        Assert.Equal(1, retryReads);
        Assert.Equal(1, queueReads);
    }

    [Theory]
    [InlineData(TriggerWorkerSelectionStatus.Empty, null, GovernedLoopLocalWorkResultStatus.Empty)]
    [InlineData(TriggerWorkerSelectionStatus.RevisionConflict, null, GovernedLoopLocalWorkResultStatus.Conflict)]
    [InlineData(TriggerWorkerSelectionStatus.ClockRollback, null, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    [InlineData(TriggerWorkerSelectionStatus.Unavailable, null, GovernedLoopLocalWorkResultStatus.Unavailable)]
    [InlineData(TriggerWorkerSelectionStatus.Acquired, TriggerWorkerMutationStatus.Committed, GovernedLoopLocalWorkResultStatus.Completed)]
    [InlineData(TriggerWorkerSelectionStatus.Acquired, TriggerWorkerMutationStatus.Replayed, GovernedLoopLocalWorkResultStatus.Completed)]
    [InlineData(TriggerWorkerSelectionStatus.Acquired, TriggerWorkerMutationStatus.NotFound, GovernedLoopLocalWorkResultStatus.Conflict)]
    [InlineData(TriggerWorkerSelectionStatus.Acquired, TriggerWorkerMutationStatus.RevisionConflict, GovernedLoopLocalWorkResultStatus.Conflict)]
    [InlineData(TriggerWorkerSelectionStatus.Acquired, TriggerWorkerMutationStatus.StaleOwner, GovernedLoopLocalWorkResultStatus.Conflict)]
    [InlineData(TriggerWorkerSelectionStatus.Acquired, TriggerWorkerMutationStatus.ClockRollback, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    [InlineData(TriggerWorkerSelectionStatus.Acquired, TriggerWorkerMutationStatus.InvalidState, GovernedLoopLocalWorkResultStatus.Corrupt)]
    [InlineData(TriggerWorkerSelectionStatus.Acquired, TriggerWorkerMutationStatus.Unavailable, GovernedLoopLocalWorkResultStatus.Unavailable)]
    public async Task Trigger_results_use_closed_typed_mapping(
        TriggerWorkerSelectionStatus selectionStatus,
        TriggerWorkerMutationStatus? mutationStatus,
        GovernedLoopLocalWorkResultStatus expectedStatus)
    {
        var runner = Runner(
            readTriggerQueue: (_, _) => Task.FromResult(Snapshot()),
            runTrigger: (_, _) => Task.FromResult(new TriggerWorkerRunResult(
                selectionStatus,
                mutationStatus,
                selectionStatus == TriggerWorkerSelectionStatus.Acquired
                    && mutationStatus != TriggerWorkerMutationStatus.NotFound
                    ? Entry("loop-1")
                    : null)));

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger);

        Assert.Equal(expectedStatus, result!.Status);
    }

    [Fact]
    public async Task Trigger_backpressure_and_malformed_results_do_not_guess_or_dispatch_again()
    {
        var calls = 0;
        var backpressured = Runner(
            readTriggerQueue: (_, _) => Task.FromResult(Snapshot(backpressured: true)),
            runTrigger: (_, _) =>
            {
                calls++;
                return Task.FromResult(new TriggerWorkerRunResult(TriggerWorkerSelectionStatus.Empty, null, null));
            });
        var malformed = Runner(
            readTriggerQueue: (_, _) => Task.FromResult(Snapshot()),
            runTrigger: static (_, _) => Task.FromResult<TriggerWorkerRunResult>(null!));

        var backpressuredResult = await backpressured.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger);
        var malformedResult = await malformed.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Backpressured, backpressuredResult!.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, malformedResult!.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Contradictory_trigger_snapshot_and_worker_payloads_fail_closed()
    {
        var contradictorySnapshot = Runner(
            readTriggerQueue: (_, _) => Task.FromResult(Snapshot() with { RetainedEntries = 1 }));
        var missingCommittedEntry = Runner(
            readTriggerQueue: (_, _) => Task.FromResult(Snapshot()),
            runTrigger: static (_, _) => Task.FromResult(
                new TriggerWorkerRunResult(
                    TriggerWorkerSelectionStatus.Acquired,
                    TriggerWorkerMutationStatus.Committed,
                    null)));

        var snapshotResult = await contradictorySnapshot.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger);
        var workerResult = await missingCommittedEntry.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, snapshotResult!.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, workerResult!.Status);
    }

    [Fact]
    public async Task Trigger_snapshot_rejects_contradictory_state_ownership_dispatch_and_boundary_shapes()
    {
        var queued = Entry("loop-1");
        var liveLease = Lease(queued);
        var releasedLease = liveLease with { ReleasedAtUtc = _now.AddSeconds(2) };
        var accepted = Dispatch(queued, liveLease, TriggerDispatchOutcome.Accepted);
        var cases = new (string Name, TriggerQueueEntry Entry)[]
        {
            ("queued-terminal-reason", queued with { TerminalReason = TriggerQueueTerminalReason.Cancelled }),
            ("not-yet-eligible-with-worker", queued with { AdmissionStatus = TriggerAdmissionStatus.NotYetEligible, AdmissionReason = TriggerAdmissionReason.NotBefore, WorkerLease = liveLease }),
            ("worker-owned-without-live-lease", queued with { State = TriggerQueueEntryState.WorkerOwned }),
            ("worker-owned-with-released-lease", queued with { State = TriggerQueueEntryState.WorkerOwned, WorkerLease = releasedLease }),
            ("dispatching-without-intent", queued with { State = TriggerQueueEntryState.Dispatching, WorkerLease = liveLease }),
            ("terminal-without-time", queued with { State = TriggerQueueEntryState.Cancelled, TerminalReason = TriggerQueueTerminalReason.Cancelled, QueuedReservationBytes = 0 }),
            ("terminal-with-wrong-reason", queued with { State = TriggerQueueEntryState.Cancelled, TerminalReason = TriggerQueueTerminalReason.Expired, QueuedReservationBytes = 0, TerminalAtUtc = _now.AddSeconds(2) }),
            ("terminal-dispatch-with-live-lease", queued with { State = TriggerQueueEntryState.Dispatched, TerminalReason = TriggerQueueTerminalReason.Dispatched, QueuedReservationBytes = 0, TerminalAtUtc = _now.AddSeconds(2), WorkerLease = liveLease, Dispatch = accepted }),
            ("order-delivery-mismatch", queued with { OrderKey = queued.OrderKey with { DeliveryId = "delivery-other" } }),
            ("undefined-priority", queued with { OrderKey = queued.OrderKey with { Priority = (TriggerQueuePriority)int.MaxValue } }),
            ("undefined-admission", queued with { AdmissionStatus = (TriggerAdmissionStatus)int.MaxValue }),
            ("entry-reservation-over-quota", queued with { RetainedReservationBytes = TriggerQueueQuota.Default.MaxEntryBytes + 1 }),
            ("queued-reservation-over-retained", queued with { QueuedReservationBytes = 2 }),
            ("worker-id-unbounded", queued with { State = TriggerQueueEntryState.WorkerOwned, WorkerLease = liveLease with { WorkerId = new string('w', TriggerWorkerLimits.MaxWorkerIdCharacters + 1) } }),
            ("lease-time-non-utc", queued with { State = TriggerQueueEntryState.WorkerOwned, WorkerLease = liveLease with { ExpiresAtUtc = liveLease.ExpiresAtUtc.ToOffset(TimeSpan.FromHours(1)) } }),
            ("dispatch-operation-mismatch", queued with { State = TriggerQueueEntryState.Dispatching, WorkerLease = liveLease, Dispatch = Dispatch(queued, liveLease, TriggerDispatchOutcome.IntentRecorded) with { OperationId = "wrong-operation" } }),
            ("terminal-dispatch-without-governed-receipt", queued with { State = TriggerQueueEntryState.Dispatched, TerminalReason = TriggerQueueTerminalReason.Dispatched, QueuedReservationBytes = 0, TerminalAtUtc = _now.AddSeconds(2), WorkerLease = releasedLease, Dispatch = accepted with { GovernedInvocation = null } })
        };

        foreach (var hostile in cases)
        {
            var result = await Runner(
                readTriggerQueue: (_, _) => Task.FromResult(Snapshot([hostile.Entry])))
                .RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger);

            Assert.True(result!.Status == GovernedLoopLocalWorkResultStatus.Corrupt, hostile.Name);
        }
    }

    [Fact]
    public async Task Trigger_snapshot_rejects_duplicate_unsorted_and_per_loop_over_quota_entries()
    {
        var first = Entry("loop-1", "1");
        var second = Entry("loop-1", "2");
        var duplicate = Runner(readTriggerQueue: (_, _) => Task.FromResult(Snapshot([first, first])));
        var unsorted = Runner(readTriggerQueue: (_, _) => Task.FromResult(Snapshot([second, first])));
        var overLoopQuota = Runner(
            readTriggerQueue: (_, _) => Task.FromResult(Snapshot(
                Enumerable.Range(1, TriggerQueueQuota.Default.MaxQueuedEntriesPerLoop + 1)
                    .Select(index => Entry("loop-1", index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    .ToArray())));

        Assert.Equal(
            GovernedLoopLocalWorkResultStatus.Corrupt,
            (await duplicate.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger))!.Status);
        Assert.Equal(
            GovernedLoopLocalWorkResultStatus.Corrupt,
            (await unsorted.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger))!.Status);
        Assert.Equal(
            GovernedLoopLocalWorkResultStatus.Corrupt,
            (await overLoopQuota.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger))!.Status);
    }

    [Fact]
    public async Task Trigger_snapshot_accepts_canonical_queued_owned_dispatching_and_terminal_shapes()
    {
        var queued = Entry("loop-1", "1");
        var notYet = Entry("loop-2", "2") with
        {
            AdmissionStatus = TriggerAdmissionStatus.NotYetEligible,
            AdmissionReason = TriggerAdmissionReason.NotBefore
        };
        var ownedBase = Entry("loop-3", "3");
        var ownedLease = Lease(ownedBase);
        var owned = ownedBase with { State = TriggerQueueEntryState.WorkerOwned, WorkerLease = ownedLease };
        var dispatchingBase = Entry("loop-4", "4");
        var dispatchingLease = Lease(dispatchingBase);
        var dispatching = dispatchingBase with
        {
            State = TriggerQueueEntryState.Dispatching,
            WorkerLease = dispatchingLease,
            Dispatch = Dispatch(dispatchingBase, dispatchingLease, TriggerDispatchOutcome.IntentRecorded)
        };
        var terminalBase = Entry("loop-5", "5");
        var terminalLease = Lease(terminalBase);
        var releasedLease = terminalLease with { ReleasedAtUtc = _now.AddSeconds(2) };
        var terminal = terminalBase with
        {
            State = TriggerQueueEntryState.Dispatched,
            TerminalReason = TriggerQueueTerminalReason.Dispatched,
            QueuedReservationBytes = 0,
            TerminalAtUtc = _now.AddSeconds(2),
            WorkerLease = releasedLease,
            Dispatch = Dispatch(terminalBase, terminalLease, TriggerDispatchOutcome.Accepted)
        };
        var workerCalls = 0;
        var runner = Runner(
            readTriggerQueue: (_, _) => Task.FromResult(Snapshot([queued, notYet, owned, dispatching, terminal])),
            runTrigger: (_, _) =>
            {
                workerCalls++;
                return Task.FromResult(new TriggerWorkerRunResult(TriggerWorkerSelectionStatus.Empty, null, null));
            });

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, result!.Status);
        Assert.Equal(1, workerCalls);
    }

    [Fact]
    public async Task Trigger_snapshot_accepts_every_canonical_terminal_state_and_admission_pair()
    {
        var replayed = Entry("loop-a", "a") with
        {
            AdmissionStatus = TriggerAdmissionStatus.Replayed,
            AdmissionReason = TriggerAdmissionReason.ExactReplay
        };
        var rejected = TerminalWithoutWorker(
            Entry("loop-b", "b") with
            {
                AdmissionStatus = TriggerAdmissionStatus.Conflicting,
                AdmissionReason = TriggerAdmissionReason.IdentityConflict
            },
            TriggerQueueEntryState.Rejected,
            TriggerQueueTerminalReason.AdmissionRejected);
        var backpressured = TerminalWithoutWorker(
            Entry("loop-c", "c"),
            TriggerQueueEntryState.Backpressured,
            TriggerQueueTerminalReason.QueueCountExceeded);
        var cancelled = TerminalWithoutWorker(
            Entry("loop-d", "d"),
            TriggerQueueEntryState.Cancelled,
            TriggerQueueTerminalReason.Cancelled);
        var expired = TerminalWithoutWorker(
            Entry("loop-e", "e") with
            {
                AdmissionStatus = TriggerAdmissionStatus.Expired,
                AdmissionReason = TriggerAdmissionReason.Expired
            },
            TriggerQueueEntryState.Expired,
            TriggerQueueTerminalReason.Expired);
        var unauthorized = TerminalWithoutWorker(
            Entry("loop-f", "f") with
            {
                AdmissionStatus = TriggerAdmissionStatus.Unauthorized,
                AdmissionReason = TriggerAdmissionReason.AuthorityMismatch
            },
            TriggerQueueEntryState.Rejected,
            TriggerQueueTerminalReason.AdmissionRejected);
        var invalid = TerminalWithoutWorker(
            Entry("loop-g", "g") with
            {
                AdmissionStatus = TriggerAdmissionStatus.Invalid,
                AdmissionReason = TriggerAdmissionReason.InvalidEnvelope
            },
            TriggerQueueEntryState.Rejected,
            TriggerQueueTerminalReason.AdmissionRejected);
        var rejectedDispatch = TerminalDispatch(
            Entry("loop-h", "h"),
            TriggerQueueEntryState.DispatchRejected,
            TriggerQueueTerminalReason.DispatchRejected,
            TriggerDispatchOutcome.Rejected);
        var needsReview = TerminalDispatch(
            Entry("loop-i", "i"),
            TriggerQueueEntryState.NeedsReview,
            TriggerQueueTerminalReason.AmbiguousDispatch,
            TriggerDispatchOutcome.NeedsReview);
        var runner = Runner(
            readTriggerQueue: (_, _) => Task.FromResult(Snapshot(
                [replayed, rejected, backpressured, cancelled, expired, unauthorized, invalid, rejectedDispatch, needsReview])));

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, result!.Status);
    }

    [Fact]
    public async Task Trigger_conflict_without_an_entry_remains_a_truthful_conflict()
    {
        var runner = Runner(
            readTriggerQueue: (_, _) => Task.FromResult(Snapshot()),
            runTrigger: static (_, _) => Task.FromResult(
                new TriggerWorkerRunResult(
                    TriggerWorkerSelectionStatus.Acquired,
                    TriggerWorkerMutationStatus.NotFound,
                    null)));

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Conflict, result!.Status);
    }

    [Fact]
    public async Task Wake_candidates_alternate_reconciliation_and_new_delivery_without_retrying_inside_a_call()
    {
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var source = Source(
            GovernedLoopBackgroundWorkReadStatus.Found,
            wakes: [new GovernedLoopWakeRequest(hashA, hashB)],
            reconciliations: [new GovernedLoopWakeReconciliationRequest(hashA, hashB)]);
        var calls = new List<string>();
        var runner = Runner(
            source,
            wake: (_, _) =>
            {
                calls.Add("wake");
                return Task.FromResult(WakeResult(GovernedLoopWakeResultStatus.Committed));
            },
            reconcileWake: (_, _) =>
            {
                calls.Add("reconcile");
                return Task.FromResult(WakeResult(GovernedLoopWakeResultStatus.Committed));
            });

        var first = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);
        var second = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, first!.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, second!.Status);
        Assert.Equal(["reconcile", "wake"], calls);
    }

    [Fact]
    public async Task Wake_rotation_prevents_a_review_blocked_first_candidate_from_starving_a_committable_candidate()
    {
        var firstId = new string('a', 64);
        var secondId = new string('b', 64);
        var checkpointHash = new string('c', 64);
        var source = Source(
            GovernedLoopBackgroundWorkReadStatus.Found,
            wakes:
            [
                new GovernedLoopWakeRequest(firstId, checkpointHash),
                new GovernedLoopWakeRequest(secondId, checkpointHash)
            ]);
        var observed = new List<string>();
        var runner = Runner(
            source,
            wake: (request, _) =>
            {
                observed.Add(request!.CheckpointId);
                var status = request.CheckpointId == firstId
                    ? GovernedLoopWakeResultStatus.ReviewBlocked
                    : GovernedLoopWakeResultStatus.Committed;
                return Task.FromResult(WakeResult(status));
            });

        var first = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);
        var second = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.AttentionRequired, first!.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, second!.Status);
        Assert.Equal([firstId, secondId], observed);
    }

    [Fact]
    public async Task Wake_reconciliation_rotation_prevents_an_ambiguous_first_candidate_from_starving_later_recovery()
    {
        var firstId = new string('a', 64);
        var secondId = new string('b', 64);
        var firstWakeId = new string('c', 64);
        var secondWakeId = new string('d', 64);
        var source = Source(
            GovernedLoopBackgroundWorkReadStatus.Found,
            reconciliations:
            [
                new GovernedLoopWakeReconciliationRequest(firstId, firstWakeId),
                new GovernedLoopWakeReconciliationRequest(secondId, secondWakeId)
            ]);
        var observed = new List<string>();
        var runner = Runner(
            source,
            reconcileWake: (request, _) =>
            {
                observed.Add(request!.CheckpointId);
                var status = request.CheckpointId == firstId
                    ? GovernedLoopWakeResultStatus.AmbiguousAttempt
                    : GovernedLoopWakeResultStatus.Committed;
                return Task.FromResult(WakeResult(status));
            });

        var first = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);
        var second = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.AttentionRequired, first!.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, second!.Status);
        Assert.Equal([firstId, secondId], observed);
    }

    [Theory]
    [InlineData(GovernedLoopWakeResultStatus.Committed, GovernedLoopLocalWorkResultStatus.Completed)]
    [InlineData(GovernedLoopWakeResultStatus.Duplicate, GovernedLoopLocalWorkResultStatus.Completed)]
    [InlineData(GovernedLoopWakeResultStatus.NotEligible, GovernedLoopLocalWorkResultStatus.Empty)]
    [InlineData(GovernedLoopWakeResultStatus.Late, GovernedLoopLocalWorkResultStatus.Completed)]
    [InlineData(GovernedLoopWakeResultStatus.Stale, GovernedLoopLocalWorkResultStatus.Completed)]
    [InlineData(GovernedLoopWakeResultStatus.Conflict, GovernedLoopLocalWorkResultStatus.Conflict)]
    [InlineData(GovernedLoopWakeResultStatus.Cancelled, GovernedLoopLocalWorkResultStatus.Completed)]
    [InlineData(GovernedLoopWakeResultStatus.Expired, GovernedLoopLocalWorkResultStatus.Completed)]
    [InlineData(GovernedLoopWakeResultStatus.Paused, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    [InlineData(GovernedLoopWakeResultStatus.ReviewBlocked, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    [InlineData(GovernedLoopWakeResultStatus.AmbiguousAttempt, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    [InlineData(GovernedLoopWakeResultStatus.Failed, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    [InlineData(GovernedLoopWakeResultStatus.Invalid, GovernedLoopLocalWorkResultStatus.Corrupt)]
    [InlineData(GovernedLoopWakeResultStatus.NotFound, GovernedLoopLocalWorkResultStatus.Empty)]
    [InlineData(GovernedLoopWakeResultStatus.Unavailable, GovernedLoopLocalWorkResultStatus.Unavailable)]
    public async Task Wake_results_use_closed_typed_mapping(
        GovernedLoopWakeResultStatus wakeStatus,
        GovernedLoopLocalWorkResultStatus expectedStatus)
    {
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var source = Source(
            GovernedLoopBackgroundWorkReadStatus.Found,
            wakes: [new GovernedLoopWakeRequest(hashA, hashB)]);
        var runner = Runner(
            source,
            wake: (_, _) => Task.FromResult(WakeResult(wakeStatus)));

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Equal(expectedStatus, result!.Status);
    }

    [Theory]
    [InlineData(GovernedLoopWakeResultStatus.NotEligible, GovernedLoopWakeDisposition.Prepared, GovernedLoopLocalWorkResultStatus.Empty)]
    [InlineData(GovernedLoopWakeResultStatus.Unavailable, GovernedLoopWakeDisposition.Prepared, GovernedLoopLocalWorkResultStatus.Unavailable)]
    [InlineData(GovernedLoopWakeResultStatus.Paused, GovernedLoopWakeDisposition.Prepared, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    [InlineData(GovernedLoopWakeResultStatus.ReviewBlocked, GovernedLoopWakeDisposition.AmbiguousAttempt, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    [InlineData(GovernedLoopWakeResultStatus.Conflict, GovernedLoopWakeDisposition.Failed, GovernedLoopLocalWorkResultStatus.Conflict)]
    [InlineData(GovernedLoopWakeResultStatus.Invalid, GovernedLoopWakeDisposition.Prepared, GovernedLoopLocalWorkResultStatus.Corrupt)]
    [InlineData(GovernedLoopWakeResultStatus.NotFound, GovernedLoopWakeDisposition.AmbiguousAttempt, GovernedLoopLocalWorkResultStatus.Empty)]
    [InlineData(GovernedLoopWakeResultStatus.Late, GovernedLoopWakeDisposition.Prepared, GovernedLoopLocalWorkResultStatus.Completed)]
    public async Task Wake_results_accept_canonical_recovery_statuses_with_current_evidence(
        GovernedLoopWakeResultStatus status,
        GovernedLoopWakeDisposition disposition,
        GovernedLoopLocalWorkResultStatus expectedStatus)
    {
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var runner = Runner(
            Source(
                GovernedLoopBackgroundWorkReadStatus.Found,
                wakes: [new GovernedLoopWakeRequest(hashA, hashB)]),
            wake: (_, _) => Task.FromResult(new GovernedLoopWakeResult(status, WakeEvidence(disposition))));

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Equal(expectedStatus, result!.Status);
    }

    [Theory]
    [InlineData(GovernedLoopWakeResultStatus.Committed, GovernedLoopWakeDisposition.Prepared)]
    [InlineData(GovernedLoopWakeResultStatus.Duplicate, GovernedLoopWakeDisposition.Failed)]
    [InlineData(GovernedLoopWakeResultStatus.NotEligible, GovernedLoopWakeDisposition.Committed)]
    [InlineData(GovernedLoopWakeResultStatus.Failed, GovernedLoopWakeDisposition.Prepared)]
    [InlineData(GovernedLoopWakeResultStatus.Stale, GovernedLoopWakeDisposition.Committed)]
    public async Task Wake_results_reject_impossible_terminal_and_recovery_evidence_pairs(
        GovernedLoopWakeResultStatus status,
        GovernedLoopWakeDisposition disposition)
    {
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var runner = Runner(
            Source(
                GovernedLoopBackgroundWorkReadStatus.Found,
                wakes: [new GovernedLoopWakeRequest(hashA, hashB)]),
            wake: (_, _) => Task.FromResult(new GovernedLoopWakeResult(status, WakeEvidence(disposition))));

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, result!.Status);
    }

    [Fact]
    public async Task Late_wake_requires_the_existing_claimants_valid_evidence()
    {
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var runner = Runner(
            Source(
                GovernedLoopBackgroundWorkReadStatus.Found,
                wakes: [new GovernedLoopWakeRequest(hashA, hashB)]),
            wake: static (_, _) => Task.FromResult(
                new GovernedLoopWakeResult(GovernedLoopWakeResultStatus.Late)));

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, result!.Status);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_at_the_subsystem_safe_boundary()
    {
        Assert.True(ScheduleId.TryParse("schedule-1", out var scheduleId));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = Runner(Source(GovernedLoopBackgroundWorkReadStatus.Found, [scheduleId!]));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule, cancellation.Token));
    }

    [Theory]
    [InlineData("", 60, 2, 4)]
    [InlineData("bad worker", 60, 2, 4)]
    [InlineData("worker", 0, 2, 4)]
    [InlineData("worker", 301, 2, 4)]
    [InlineData("worker", 60, 0, 4)]
    [InlineData("worker", 60, 33, 4)]
    [InlineData("worker", 60, 2, 0)]
    [InlineData("worker", 60, 2, 257)]
    public void Invalid_runner_options_fail_before_any_background_work(
        string workerId,
        int leaseSeconds,
        int consecutive,
        int candidateLimit)
    {
        var options = new GovernedLoopLocalWorkRunnerOptions(
            workerId,
            TimeSpan.FromSeconds(leaseSeconds),
            consecutive,
            candidateLimit);

        Assert.ThrowsAny<ArgumentException>(() => Runner(options: options));
    }

    [Fact]
    public async Task Invalid_family_and_clock_posture_fail_closed_without_touching_dependencies()
    {
        var source = new ScriptedBackgroundWorkSource();
        var invalidFamily = Runner(source);
        var throwingClock = new SteppingCoordinatorTimeProvider(_now, TimeSpan.Zero) { ThrowOnNext = true };
        var unavailableClock = Runner(source, timeProvider: throwingClock);
        var corruptClock = Runner(
            source,
            timeProvider: new SteppingCoordinatorTimeProvider(_now.ToOffset(TimeSpan.FromHours(1)), TimeSpan.Zero));

        var familyResult = await invalidFamily.RunOnceAsync((GovernedLoopLocalWorkFamily)999);
        var clockResult = await unavailableClock.RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule);
        var corruptClockResult = await corruptClock.RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, familyResult!.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Unavailable, clockResult!.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, corruptClockResult!.Status);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Readiness_probe_accepts_healthy_pending_schedule_and_wake_work_without_actuating_it()
    {
        Assert.True(ScheduleId.TryParse("schedule-1", out var scheduleId));
        var wake = new GovernedLoopWakeRequest(new string('a', 64), new string('b', 64));
        var source = Source(
            GovernedLoopBackgroundWorkReadStatus.Found,
            [scheduleId!],
            [wake]);
        var runner = Runner(source);

        var schedule = await runner.ProbeReadinessAsync(GovernedLoopLocalWorkFamily.Schedule);
        var wakeResult = await runner.ProbeReadinessAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, schedule!.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, wakeResult!.Status);
        Assert.Equal(2, source.Calls);
    }

    private static GovernedLoopLocalWorkRunner Runner(
        ScriptedBackgroundWorkSource? source = null,
        Func<ScheduleId, CancellationToken, Task<ScheduleEvaluationResult>>? evaluateSchedule = null,
        Func<DateTimeOffset, CancellationToken, Task<GovernedLoopLocalWorkResult>>? retryScheduleAdmission = null,
        Func<DateTimeOffset, CancellationToken, Task<TriggerQueueSnapshot>>? readTriggerQueue = null,
        Func<TriggerWorkerRunRequest, CancellationToken, Task<TriggerWorkerRunResult>>? runTrigger = null,
        Func<GovernedLoopWakeRequest?, CancellationToken, Task<GovernedLoopWakeResult>>? wake = null,
        Func<GovernedLoopWakeReconciliationRequest?, CancellationToken, Task<GovernedLoopWakeResult>>? reconcileWake = null,
        GovernedLoopLocalWorkRunnerOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        var oneShot = new ScriptedLocalOneShotServices
        {
            EvaluateSchedule = evaluateSchedule is null
                ? static (scheduleId, _) => Task.FromResult<ScheduleEvaluationResult?>(ScheduleResult(ScheduleEvaluationStatus.NotDue, scheduleId))
                : (scheduleId, cancellationToken) => Wrap(evaluateSchedule(scheduleId, cancellationToken)),
            RetryScheduleAdmission = retryScheduleAdmission is null
                ? static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(
                    new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Empty, "schedule-retry-empty"))
                : async (observedAtUtc, cancellationToken) => await retryScheduleAdmission(observedAtUtc, cancellationToken).ConfigureAwait(false),
            ReadTriggerQueue = readTriggerQueue is null
                ? static (_, _) => Task.FromResult<TriggerQueueSnapshot?>(Snapshot())
                : async (observedAtUtc, cancellationToken) => await readTriggerQueue(observedAtUtc, cancellationToken).ConfigureAwait(false),
            RunTrigger = runTrigger is null
                ? static (_, _) => Task.FromResult<TriggerWorkerRunResult?>(new TriggerWorkerRunResult(TriggerWorkerSelectionStatus.Empty, null, null))
                : async (request, cancellationToken) => await runTrigger(request, cancellationToken).ConfigureAwait(false),
            Wake = wake is null
                ? static (_, _) => Task.FromResult<GovernedLoopWakeResult?>(new GovernedLoopWakeResult(GovernedLoopWakeResultStatus.NotEligible))
                : async (request, cancellationToken) => await wake(request, cancellationToken).ConfigureAwait(false),
            ReconcileWake = reconcileWake is null
                ? static (_, _) => Task.FromResult<GovernedLoopWakeResult?>(new GovernedLoopWakeResult(GovernedLoopWakeResultStatus.NotEligible))
                : async (request, cancellationToken) => await reconcileWake(request, cancellationToken).ConfigureAwait(false)
        };
        return new GovernedLoopLocalWorkRunner(
            source ?? new ScriptedBackgroundWorkSource(),
            oneShot,
            options ?? Options(),
            timeProvider ?? new SteppingCoordinatorTimeProvider(_now, TimeSpan.Zero));
    }

    private static async Task<ScheduleEvaluationResult?> Wrap(Task<ScheduleEvaluationResult> task)
        => await task.ConfigureAwait(false);

    private static GovernedLoopLocalWorkRunnerOptions Options()
        => new("local-worker", TimeSpan.FromMinutes(1), 2, 4);

    private static ScriptedBackgroundWorkSource Source(
        GovernedLoopBackgroundWorkReadStatus status,
        IReadOnlyList<ScheduleId>? schedules = null,
        IReadOnlyList<GovernedLoopWakeRequest>? wakes = null,
        IReadOnlyList<GovernedLoopWakeReconciliationRequest>? reconciliations = null)
        => new()
        {
            Handler = (_, _, _, _) => Task.FromResult<GovernedLoopBackgroundWorkReadResult?>(
                GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
                    status,
                    schedules ?? [],
                    wakes ?? [],
                    reconciliations ?? []))
        };

    private static TriggerQueueSnapshot Snapshot(long generation = 1, bool backpressured = false)
        => Snapshot([], generation: generation, backpressured: backpressured);

    private static TriggerQueueSnapshot Snapshot(
        IReadOnlyList<TriggerQueueEntry> entries,
        TriggerQueueQuota? quota = null,
        long generation = 1,
        bool backpressured = false)
    {
        var exactQuota = quota ?? TriggerQueueQuota.Default;
        var nonterminal = entries.Where(entry => entry.State is TriggerQueueEntryState.Queued or TriggerQueueEntryState.WorkerOwned or TriggerQueueEntryState.Dispatching).ToArray();
        return new TriggerQueueSnapshot(
            TriggerQueueSnapshot.CurrentSchemaVersion,
            generation,
            exactQuota,
            nonterminal.Length,
            nonterminal.Sum(entry => (long)entry.SerializedEntryBytes),
            nonterminal.Sum(entry => (long)entry.QueuedReservationBytes),
            entries.Count,
            entries.Sum(entry => (long)entry.SerializedEntryBytes),
            entries.Sum(entry => (long)entry.RetainedReservationBytes),
            0,
            backpressured,
            entries);
    }

    private static TriggerQueueEntry Entry(string loopId, string identitySuffix = "1")
    {
        Assert.True(TriggerDeliveryId.TryParse($"delivery-{identitySuffix}", out var deliveryId));
        Assert.True(TriggerDeduplicationId.TryParse($"deduplication-{identitySuffix}", out var deduplicationId));
        return new TriggerQueueEntry(
            deliveryId!,
            deduplicationId!,
            loopId,
            new string('a', 64),
            1,
            1,
            1,
            TriggerQueueEntryState.Queued,
            TriggerQueueTerminalReason.None,
            new TriggerQueueOrderKey(_now, TriggerQueuePriority.Normal, _now, deliveryId!.Value),
            1,
            _now,
            null,
            TriggerAdmissionStatus.Admitted,
            TriggerAdmissionReason.EvidenceAccepted);
    }

    private static TriggerWorkerLease Lease(TriggerQueueEntry entry)
        => new(
            "worker-1",
            1,
            entry.RecordedAtUtc,
            entry.RecordedAtUtc.AddMinutes(1),
            0);

    private static TriggerDispatchEvidence Dispatch(
        TriggerQueueEntry entry,
        TriggerWorkerLease lease,
        TriggerDispatchOutcome outcome)
    {
        var operationId = TriggerWorkerRequestHash.ComputeOperationId(entry.DeliveryId, lease.Generation);
        var terminal = outcome is TriggerDispatchOutcome.Accepted
            or TriggerDispatchOutcome.Terminal
            or TriggerDispatchOutcome.Rejected
            or TriggerDispatchOutcome.NeedsReview;
        var governed = outcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal
            ? new TriggerGovernedInvocationEvidence(
                operationId,
                "run-1",
                new string('d', 64),
                entry.LoopId,
                new string('e', 64))
            : null;
        return new TriggerDispatchEvidence(
            operationId,
            new string('b', 64),
            new string('c', 64),
            lease.AcquiredAtUtc,
            outcome,
            terminal ? lease.AcquiredAtUtc.AddSeconds(1) : null,
            "test-dispatch",
            governed);
    }

    private static TriggerQueueEntry TerminalWithoutWorker(
        TriggerQueueEntry entry,
        TriggerQueueEntryState state,
        TriggerQueueTerminalReason reason)
        => entry with
        {
            State = state,
            TerminalReason = reason,
            QueuedReservationBytes = 0,
            TerminalAtUtc = entry.RecordedAtUtc.AddSeconds(2)
        };

    private static TriggerQueueEntry TerminalDispatch(
        TriggerQueueEntry entry,
        TriggerQueueEntryState state,
        TriggerQueueTerminalReason reason,
        TriggerDispatchOutcome outcome)
    {
        var lease = Lease(entry);
        return entry with
        {
            State = state,
            TerminalReason = reason,
            QueuedReservationBytes = 0,
            TerminalAtUtc = entry.RecordedAtUtc.AddSeconds(2),
            WorkerLease = lease with { ReleasedAtUtc = entry.RecordedAtUtc.AddSeconds(2) },
            Dispatch = Dispatch(entry, lease, outcome)
        };
    }

    private static ScheduleEvaluationResult ScheduleResult(
        ScheduleEvaluationStatus status,
        ScheduleId scheduleId)
        => new(
            status,
            "test-result",
            status is ScheduleEvaluationStatus.NotFound
                or ScheduleEvaluationStatus.Unavailable
                or ScheduleEvaluationStatus.Corrupt
                or ScheduleEvaluationStatus.Unknown
                ? null
                : CreateScheduleState(scheduleId, status));

    private static ScheduleState CreateScheduleState(
        ScheduleId scheduleId,
        ScheduleEvaluationStatus status)
    {
        var nextOccurrence = status == ScheduleEvaluationStatus.Exhausted
            ? null
            : new ScheduleOccurrence(
                ScheduleOccurrence.CurrentSchemaVersion,
                1,
                DateTime.SpecifyKind(_now.UtcDateTime.AddHours(1), DateTimeKind.Unspecified),
                _now.AddHours(1),
                new ScheduleTimeZoneReference("Etc/UTC", new string('1', 64)));
        var state = new ScheduleState(
            ScheduleState.CurrentSchemaVersion,
            scheduleId,
            1,
            new string('2', 64),
            1,
            status != ScheduleEvaluationStatus.Disabled,
            nextOccurrence,
            null,
            null,
            _now,
            null,
            [],
            []);
        Assert.True(ScheduleContractValidator.ValidateState(state).IsValid);
        return state;
    }

    private static GovernedLoopWakeResult WakeResult(GovernedLoopWakeResultStatus status)
    {
        var disposition = status switch
        {
            GovernedLoopWakeResultStatus.Committed => GovernedLoopWakeDisposition.Committed,
            GovernedLoopWakeResultStatus.Duplicate => GovernedLoopWakeDisposition.Duplicate,
            GovernedLoopWakeResultStatus.Late => GovernedLoopWakeDisposition.Late,
            GovernedLoopWakeResultStatus.Stale => GovernedLoopWakeDisposition.Stale,
            GovernedLoopWakeResultStatus.Conflict => GovernedLoopWakeDisposition.Conflict,
            GovernedLoopWakeResultStatus.Cancelled => GovernedLoopWakeDisposition.Cancelled,
            GovernedLoopWakeResultStatus.Expired => GovernedLoopWakeDisposition.Expired,
            GovernedLoopWakeResultStatus.Paused => GovernedLoopWakeDisposition.Paused,
            GovernedLoopWakeResultStatus.ReviewBlocked => GovernedLoopWakeDisposition.ReviewBlocked,
            GovernedLoopWakeResultStatus.AmbiguousAttempt => GovernedLoopWakeDisposition.AmbiguousAttempt,
            GovernedLoopWakeResultStatus.Failed => GovernedLoopWakeDisposition.Failed,
            _ => (GovernedLoopWakeDisposition?)null
        };
        return new GovernedLoopWakeResult(
            status,
            disposition is null ? null : WakeEvidence(disposition.Value));
    }

    private static GovernedLoopWakeEvidence WakeEvidence(GovernedLoopWakeDisposition disposition)
    {
        var shape = disposition switch
        {
            GovernedLoopWakeDisposition.Prepared => ("continuation-operation-1", (string?)null, (string?)null),
            GovernedLoopWakeDisposition.Committed => ("continuation-operation-1", new string('e', 64), (string?)null),
            GovernedLoopWakeDisposition.AmbiguousAttempt => ("continuation-operation-1", (string?)null, "ambiguity-evidence-1"),
            GovernedLoopWakeDisposition.Failed => ((string?)null, (string?)null, "failure-evidence-1"),
            _ => ((string?)null, (string?)null, "disposition-evidence-1")
        };
        return GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeEvidence(
            GovernedLoopWakeEvidence.CurrentSchemaVersion,
            1,
            WakeIdentity(),
            disposition,
            shape.Item1,
            shape.Item2,
            shape.Item3,
            _now.AddHours(1),
            string.Empty));
    }

    private static GovernedLoopWakeIdentity WakeIdentity()
    {
        var revision = GovernedLoopRevisionReference.Create(1, "graph-1", "revision-1", new string('1', 64));
        var execution = GovernedLoopExecutionBinding.Create(1, "run-1", revision, 1);
        var publication = new GovernedLoopRevisionPublicationPin(
            1,
            revision,
            "publication-operation-1",
            new string('2', 64));
        var binding = new GovernedLoopSleepBinding(
            execution,
            publication,
            1,
            new string('3', 64),
            1,
            null,
            null,
            "wait-node",
            1,
            1,
            "wait-operation-1");
        var checkpoint = GovernedLoopSleepContractHash.Apply(new GovernedLoopSleepCheckpoint(
            GovernedLoopSleepCheckpoint.CurrentSchemaVersion,
            string.Empty,
            binding,
            GovernedLoopWakeMode.Timestamp,
            _now.AddHours(1),
            null,
            _now,
            string.Empty));
        return GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeIdentity(
            GovernedLoopWakeIdentity.CurrentSchemaVersion,
            string.Empty,
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            checkpoint.WakeMode,
            null,
            null,
            string.Empty));
    }
}
