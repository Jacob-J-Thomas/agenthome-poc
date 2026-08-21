using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Tests.Loops.Sleep;
using EmbodySense.Core.Application.Tests.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Posture.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Posture;

public sealed class GovernedLoopOperationalPostureServiceTests
{
    private static readonly DateTimeOffset _now = ScheduleEvaluatorTestData.Now;
    private static readonly string _workspaceId = "workspace-sha256:" + new string('1', 64);
    private static readonly string _triggerWorkspaceId = new('1', 64);

    [Fact]
    public async Task Available_projection_exposes_positions_due_wakes_and_live_worker_without_sensitive_values()
    {
        var queue = new StubOperationalTriggerQueuePort { Snapshot = QueueSnapshot(QueueEntry()) };
        var definition = Definition();
        var state = ScheduleEvaluatorTestData.State(definition);
        var schedules = new StubOperationalSchedulePort
        {
            Result = new GovernedLoopScheduleEvidenceReadResult(
                GovernedLoopOperationalEvidenceReadStatus.Found,
                7,
                false,
                null,
                [new GovernedLoopScheduleEvidenceSnapshot(definition, state)])
        };
        var checkpoint = Checkpoint();
        var wakes = new StubOperationalWakePort
        {
            Result = new GovernedLoopWakeCatalogEvidenceReadResult(
                GovernedLoopOperationalEvidenceReadStatus.Found,
                8,
                false,
                null,
                [new GovernedLoopWakeEvidenceSnapshot(checkpoint, null)])
        };
        var coordinator = new StubOperationalCoordinatorPort { Result = Coordinator() };
        var service = Service(queue, schedules, wakes, coordinator);

        var result = await service.ReadAsync(new GovernedLoopOperationalPostureQuery(10, 10, 10, 10));

        Assert.Equal(GovernedLoopOperationalPostureReadStatus.Available, result.Status);
        Assert.Equal("operational-posture-available", result.ReasonCode);
        var snapshot = Assert.IsType<GovernedLoopOperationalPostureSnapshot>(result.Snapshot);
        Assert.Null(Assert.Single(snapshot.Queue.Items).QueuePosition);
        Assert.Equal("queued", snapshot.Queue.Items[0].State);
        var schedule = Assert.Single(snapshot.Schedules.Items);
        Assert.Equal("due", schedule.State);
        Assert.True(schedule.Enabled);
        Assert.True(ScheduleContractHash.TryComputeState(state, out var expectedStateHash, out var stateValidation), ScheduleEvaluatorTestData.Errors(stateValidation));
        Assert.Equal(expectedStateHash, schedule.EvidenceHash);
        Assert.Equal("due", Assert.Single(snapshot.Wakes.Items).State);
        Assert.Equal("running", snapshot.Coordinator.State);
        Assert.Equal(1, snapshot.Coordinator.OwnershipEpoch);
        Assert.DoesNotContain("payload", System.Text.Json.JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("event-subscription", System.Text.Json.JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finite_pages_preserve_complete_totals_and_report_truncation()
    {
        var entries = new[] { QueueEntry("delivery-1", "deduplication-1"), QueueEntry("delivery-2", "deduplication-2", _now.AddSeconds(1)) };
        var queue = new StubOperationalTriggerQueuePort { Snapshot = QueueSnapshot(entries) };
        var service = Service(
            queue,
            new StubOperationalSchedulePort(),
            new StubOperationalWakePort(),
            new StubOperationalCoordinatorPort());

        var result = await service.ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));

        var posture = Assert.IsType<GovernedLoopOperationalPostureSnapshot>(result.Snapshot).Queue;
        Assert.True(posture.HasMore);
        Assert.Equal(2, posture.QueuedEntries);
        Assert.Single(posture.Items);
    }

    [Fact]
    public async Task Queue_pages_preserve_canonical_evidence_order_without_claiming_worker_rank()
    {
        var entries = new[]
        {
            QueueEntry("delivery-z", "deduplication-z", priority: TriggerQueuePriority.Critical),
            QueueEntry("delivery-a", "deduplication-a", priority: TriggerQueuePriority.Background)
        };
        var queue = new StubOperationalTriggerQueuePort { Snapshot = QueueSnapshot(entries) };
        var service = Service(queue, new StubOperationalSchedulePort(), new StubOperationalWakePort(), new StubOperationalCoordinatorPort());

        var firstResult = await service.ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));
        var first = Assert.IsType<GovernedLoopOperationalPostureSnapshot>(firstResult.Snapshot).Queue;
        var secondResult = await service.ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1, first.ContinuationCursor));
        var second = Assert.IsType<GovernedLoopOperationalPostureSnapshot>(secondResult.Snapshot).Queue;

        Assert.Equal("delivery-z", Assert.Single(first.Items).DeliveryId);
        Assert.Equal("delivery-a", Assert.Single(second.Items).DeliveryId);
        Assert.Null(first.Items[0].QueuePosition);
        Assert.Null(second.Items[0].QueuePosition);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task Expired_worker_lease_projects_blocked_attention_without_claiming_a_terminal_transition()
    {
        var entry = QueueEntry() with
        {
            State = TriggerQueueEntryState.WorkerOwned,
            Revision = 2,
            WorkerLease = new TriggerWorkerLease("worker-1", 1, _now.AddMinutes(-2), _now.AddMinutes(-1), 0)
        };
        var service = Service(
            new StubOperationalTriggerQueuePort { Snapshot = QueueSnapshot(entry) },
            new StubOperationalSchedulePort(),
            new StubOperationalWakePort(),
            new StubOperationalCoordinatorPort());

        var result = await service.ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));

        var item = Assert.Single(Assert.IsType<GovernedLoopOperationalPostureSnapshot>(result.Snapshot).Queue.Items);
        Assert.Equal("blocked", item.State);
        Assert.Equal("trigger-worker-lease-expired", item.ReasonCode);
        Assert.True(item.WorkerLeaseExpired);
        Assert.Null(item.QueuePosition);
        Assert.Equal(2, item.Revision);
    }

    [Fact]
    public async Task Immutable_disabled_schedule_advertises_no_enable_control()
    {
        var definition = Definition() with { Enabled = false };
        var state = ScheduleEvaluatorTestData.State(definition);

        var result = await ServiceWithSchedule(definition, state).ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));

        var schedule = Assert.Single(Assert.IsType<GovernedLoopOperationalPostureSnapshot>(result.Snapshot).Schedules.Items);
        Assert.Equal("disabled", schedule.State);
        Assert.False(schedule.Enabled);
        Assert.Empty(schedule.EligibleControls);
    }

    [Fact]
    public async Task Disabled_schedule_preserves_critical_attention_for_an_ambiguous_pending_delivery()
    {
        var definition = Definition();
        var store = new TestScheduleStore(definition, ScheduleEvaluatorTestData.State(definition));
        var evaluator = new ScheduleDueOccurrenceEvaluator(
            store,
            new TestScheduleCurrentEvidence(),
            new TestScheduleOverlap(),
            new TestScheduleTimeZone(),
            new TestScheduleQueue { Throw = true },
            new TestScheduleAdmissionHistory(),
            new TestScheduleTimeProvider(_now));
        var evaluated = await evaluator.EvaluateAsync(definition.ScheduleId);
        var disabled = evaluated.State! with { Enabled = false };
        Assert.Equal(ScheduleDeliveryResultKind.Ambiguous, disabled.PendingDelivery!.Result!.Kind);
        Assert.True(ScheduleContractValidator.ValidateDefinitionStateComposition(definition, disabled).IsValid);

        var result = await ServiceWithSchedule(definition, disabled).ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));

        var schedule = Assert.Single(Assert.IsType<GovernedLoopOperationalPostureSnapshot>(result.Snapshot).Schedules.Items);
        Assert.False(schedule.Enabled);
        Assert.Equal("needs-review", schedule.State);
        Assert.Equal("schedule-delivery-outcome-ambiguous", schedule.ReasonCode);
        Assert.Equal(GovernedLoopPostureSeverity.Critical, schedule.Severity);
        Assert.Equal("result-observed", schedule.PendingDeliveryPhase);
    }

    [Fact]
    public async Task Malformed_or_unavailable_family_evidence_fails_the_complete_projection_closed()
    {
        var queue = new StubOperationalTriggerQueuePort { Snapshot = QueueSnapshot() };
        var malformedSchedules = new StubOperationalSchedulePort
        {
            Result = new GovernedLoopScheduleEvidenceReadResult(GovernedLoopOperationalEvidenceReadStatus.Found, 1, false, null, [])
        };
        var malformed = Service(
            queue,
            malformedSchedules,
            new StubOperationalWakePort(),
            new StubOperationalCoordinatorPort());
        var unavailableSchedules = new StubOperationalSchedulePort
        {
            Result = new GovernedLoopScheduleEvidenceReadResult(GovernedLoopOperationalEvidenceReadStatus.Unavailable, 0, false, null, [])
        };
        var unavailable = Service(
            queue,
            unavailableSchedules,
            new StubOperationalWakePort(),
            new StubOperationalCoordinatorPort());

        var malformedResult = await malformed.ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));
        var unavailableResult = await unavailable.ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));

        Assert.Equal(GovernedLoopOperationalPostureReadStatus.Corrupt, malformedResult.Status);
        Assert.Null(malformedResult.Snapshot);
        Assert.Equal(GovernedLoopOperationalPostureReadStatus.Unavailable, unavailableResult.Status);
        Assert.Null(unavailableResult.Snapshot);
    }

    [Fact]
    public async Task Malformed_or_future_queue_and_run_evidence_fails_the_complete_projection_closed()
    {
        var malformedQueue = QueueEntry() with { TargetGraphId = "graph-1", TargetRevisionId = null };
        var queueService = Service(
            new StubOperationalTriggerQueuePort { Snapshot = QueueSnapshot(malformedQueue) },
            new StubOperationalSchedulePort(),
            new StubOperationalWakePort(),
            new StubOperationalCoordinatorPort());
        var futureRecordedAtUtc = _now.AddMinutes(1);
        var futureQueue = QueueEntry() with
        {
            RecordedAtUtc = futureRecordedAtUtc,
            OrderKey = new TriggerQueueOrderKey(futureRecordedAtUtc, TriggerQueuePriority.Normal, futureRecordedAtUtc, "delivery-1")
        };
        var futureQueueService = Service(
            new StubOperationalTriggerQueuePort { Snapshot = QueueSnapshot(futureQueue) },
            new StubOperationalSchedulePort(),
            new StubOperationalWakePort(),
            new StubOperationalCoordinatorPort());
        var runSummary = RunSummary();
        var malformedRuns = new StubOperationalRunPort
        {
            Result = new GovernedLoopRunEvidenceReadResult(
                GovernedLoopOperationalEvidenceReadStatus.Found,
                false,
                null,
                [new GovernedLoopRunEvidenceSnapshot(runSummary, new string('g', 257), "revision-1", new string('a', 64))])
        };
        var runService = Service(
            new StubOperationalTriggerQueuePort { Snapshot = QueueSnapshot() },
            new StubOperationalSchedulePort(),
            new StubOperationalWakePort(),
            new StubOperationalCoordinatorPort(),
            malformedRuns);
        var futureRuns = new StubOperationalRunPort
        {
            Result = new GovernedLoopRunEvidenceReadResult(
                GovernedLoopOperationalEvidenceReadStatus.Found,
                false,
                null,
                [new GovernedLoopRunEvidenceSnapshot(runSummary with { UpdatedAtUtc = _now.AddMinutes(1) }, "graph-1", "revision-1", new string('a', 64))])
        };
        var futureRunService = Service(
            new StubOperationalTriggerQueuePort { Snapshot = QueueSnapshot() },
            new StubOperationalSchedulePort(),
            new StubOperationalWakePort(),
            new StubOperationalCoordinatorPort(),
            futureRuns);

        var queueResult = await queueService.ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));
        var futureQueueResult = await futureQueueService.ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));
        var runResult = await runService.ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));
        var futureRunResult = await futureRunService.ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));

        Assert.Equal(GovernedLoopOperationalPostureReadStatus.Corrupt, queueResult.Status);
        Assert.Null(queueResult.Snapshot);
        Assert.Equal(GovernedLoopOperationalPostureReadStatus.Corrupt, futureQueueResult.Status);
        Assert.Null(futureQueueResult.Snapshot);
        Assert.Equal(GovernedLoopOperationalPostureReadStatus.Corrupt, runResult.Status);
        Assert.Null(runResult.Snapshot);
        Assert.Equal(GovernedLoopOperationalPostureReadStatus.Corrupt, futureRunResult.Status);
        Assert.Null(futureRunResult.Snapshot);
    }

    [Fact]
    public async Task Deleted_run_tombstone_remains_visible_as_valid_operational_evidence()
    {
        var deletedAtUtc = _now.AddMinutes(-1);
        var tombstone = RunSummary() with
        {
            LifecycleVersion = 0,
            Status = CustomLoopRunStatus.Completed,
            UpdatedAtUtc = deletedAtUtc,
            CompletedAtUtc = _now.AddMinutes(-2),
            Iteration = 0,
            NextStepIndex = 0,
            IsDeleted = true
        };
        var runs = new StubOperationalRunPort
        {
            Result = new GovernedLoopRunEvidenceReadResult(
                GovernedLoopOperationalEvidenceReadStatus.Found,
                false,
                null,
                [new GovernedLoopRunEvidenceSnapshot(tombstone, null, null, new string('a', 64))])
        };
        var service = Service(
            new StubOperationalTriggerQueuePort { Snapshot = QueueSnapshot() },
            new StubOperationalSchedulePort(),
            new StubOperationalWakePort(),
            new StubOperationalCoordinatorPort(),
            runs);

        var result = await service.ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));

        Assert.Equal(GovernedLoopOperationalPostureReadStatus.Available, result.Status);
        var item = Assert.Single(Assert.IsType<GovernedLoopOperationalPostureSnapshot>(result.Snapshot).Runs.Items);
        Assert.Equal("deleted", item.State);
        Assert.Equal("run-deleted", item.ReasonCode);
        Assert.Equal(0, item.LifecycleVersion);
        Assert.Empty(item.EligibleControls);
    }

    [Fact]
    public async Task Schedule_composition_and_unhashable_state_evidence_fail_the_complete_projection_closed()
    {
        var definition = Definition();
        var state = ScheduleEvaluatorTestData.State(definition);
        var mismatched = state with { DefinitionRevision = state.DefinitionRevision + 1 };
        var oversized = OversizedScheduleState(definition);
        var compositionService = ServiceWithSchedule(definition, mismatched);
        var hashService = ServiceWithSchedule(definition, oversized);

        var compositionResult = await compositionService.ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));
        var hashResult = await hashService.ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));

        Assert.Equal(GovernedLoopOperationalPostureReadStatus.Corrupt, compositionResult.Status);
        Assert.Null(compositionResult.Snapshot);
        Assert.Equal(GovernedLoopOperationalPostureReadStatus.Corrupt, hashResult.Status);
        Assert.Null(hashResult.Snapshot);
    }

    [Fact]
    public async Task Invalid_bounds_and_queue_backpressure_have_distinct_safe_outcomes()
    {
        var queue = new StubOperationalTriggerQueuePort { Snapshot = QueueSnapshot() with { PersistenceBackpressured = true } };
        var service = Service(
            queue,
            new StubOperationalSchedulePort(),
            new StubOperationalWakePort(),
            new StubOperationalCoordinatorPort());

        var invalid = await service.ReadAsync(new GovernedLoopOperationalPostureQuery(0, 1, 1, 1));
        var backpressured = await service.ReadAsync(new GovernedLoopOperationalPostureQuery(1, 1, 1, 1));

        Assert.Equal(GovernedLoopOperationalPostureReadStatus.Invalid, invalid.Status);
        Assert.Equal(GovernedLoopOperationalPostureReadStatus.Backpressured, backpressured.Status);
        Assert.NotNull(backpressured.Snapshot);
    }

    private static TriggerQueueSnapshot QueueSnapshot(params TriggerQueueEntry[] entries)
    {
        var nonterminal = entries.Count(entry => entry.State is TriggerQueueEntryState.Queued or TriggerQueueEntryState.WorkerOwned or TriggerQueueEntryState.Dispatching);
        return new TriggerQueueSnapshot(
            TriggerQueueSnapshot.CurrentSchemaVersion,
            4,
            TriggerQueueQuota.Default,
            nonterminal,
            entries.Where(entry => entry.State == TriggerQueueEntryState.Queued).Sum(entry => (long)entry.SerializedEntryBytes),
            entries.Where(entry => entry.State == TriggerQueueEntryState.Queued).Sum(entry => (long)entry.QueuedReservationBytes),
            entries.Length,
            entries.Sum(entry => (long)entry.SerializedEntryBytes),
            entries.Sum(entry => (long)entry.RetainedReservationBytes),
            0,
            false,
            entries);
    }

    private static GovernedLoopOperationalPostureService ServiceWithSchedule(ScheduleDefinition definition, ScheduleState state)
        => Service(
            new StubOperationalTriggerQueuePort { Snapshot = QueueSnapshot() },
            new StubOperationalSchedulePort
            {
                Result = new GovernedLoopScheduleEvidenceReadResult(
                    GovernedLoopOperationalEvidenceReadStatus.Found,
                    1,
                    false,
                    null,
                    [new GovernedLoopScheduleEvidenceSnapshot(definition, state)])
            },
            new StubOperationalWakePort(),
            new StubOperationalCoordinatorPort());

    private static GovernedLoopOperationalPostureService Service(
        StubOperationalTriggerQueuePort queue,
        StubOperationalSchedulePort schedules,
        StubOperationalWakePort wakes,
        StubOperationalCoordinatorPort coordinator,
        StubOperationalRunPort? runs = null)
        => new(
            _workspaceId,
            _triggerWorkspaceId,
            "background-coordinator",
            queue,
            schedules,
            wakes,
            runs ?? new StubOperationalRunPort(),
            coordinator,
            new StubOperationalControlAuthorityPort { WorkspaceId = _workspaceId, ObservedAtUtc = _now },
            new StubGovernedLoopSleepTimeProvider(_now));

    private static ScheduleDefinition Definition()
        => ScheduleEvaluatorTestData.Definition() with { WorkspaceId = _triggerWorkspaceId };

    private static CustomLoopRunSummary RunSummary()
        => new(
            "run-1",
            "loop-1",
            "admission-1",
            1,
            1,
            CustomLoopRunStatus.Running,
            _now.AddMinutes(-2),
            _now.AddMinutes(-1),
            null,
            1,
            0,
            null,
            false);

    private static ScheduleState OversizedScheduleState(ScheduleDefinition definition)
    {
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out var definitionValidation), ScheduleEvaluatorTestData.Errors(definitionValidation));
        var dispositions = Enumerable.Range(1, ScheduleContractLimits.MaxDispositionEvidenceItems)
            .Select(ordinal => Disposition(definition, ordinal))
            .ToArray();
        var terminals = Enumerable.Range(
                ScheduleContractLimits.MaxDispositionEvidenceItems + 1,
                ScheduleContractLimits.MaxTerminalDeliveryEvidenceItems)
            .Select(ordinal => Terminal(definition, definitionHash!, ordinal))
            .ToArray();
        var next = Occurrence(definition, 1000);
        var state = ScheduleEvaluatorTestData.State(
            definition,
            next,
            lastClock: next.ScheduledAtUtc.AddSeconds(5),
            dispositions: dispositions,
            terminal: terminals);
        Assert.True(ScheduleContractValidator.ValidateDefinitionStateComposition(definition, state).IsValid);
        Assert.False(ScheduleContractHash.TryComputeState(state, out _, out var validation));
        Assert.Contains(validation.Errors, error => error.Code == "canonical_document_too_large");
        return state;
    }

    private static ScheduleOccurrenceDispositionEvidence Disposition(ScheduleDefinition definition, int ordinal)
    {
        var occurrence = Occurrence(definition, ordinal);
        return new ScheduleOccurrenceDispositionEvidence(
            ScheduleOccurrenceDispositionEvidence.CurrentSchemaVersion,
            ordinal,
            ordinal,
            1,
            occurrence.ScheduledLocal,
            occurrence.ScheduledLocal,
            occurrence.ScheduledAtUtc,
            occurrence.ScheduledAtUtc,
            definition.TimeZone,
            ScheduleOccurrenceDisposition.MisfireSkipped,
            null,
            new string('r', ScheduleContractLimits.MaxReasonCodeCharacters),
            occurrence.ScheduledAtUtc.AddSeconds(1));
    }

    private static ScheduleTerminalDeliveryEvidence Terminal(ScheduleDefinition definition, string definitionHash, int ordinal)
    {
        var occurrence = Occurrence(definition, ordinal);
        Assert.True(ScheduleIdentityDerivation.TryDerive(
            definition.ScheduleId,
            definition.Revision,
            definitionHash,
            occurrence,
            out var identity,
            out var identityValidation),
            ScheduleEvaluatorTestData.Errors(identityValidation));
        var result = new ScheduleDeliveryResultEvidence(
            ScheduleDeliveryResultEvidence.CurrentSchemaVersion,
            ScheduleDeliveryResultKind.Queued,
            new string('q', ScheduleContractLimits.MaxReasonCodeCharacters),
            new string('7', ScheduleContractLimits.Sha256HexCharacters),
            occurrence.ScheduledAtUtc.AddSeconds(1));
        return new ScheduleTerminalDeliveryEvidence(
            ScheduleTerminalDeliveryEvidence.CurrentSchemaVersion,
            occurrence,
            identity!,
            new string('f', ScheduleContractLimits.Sha256HexCharacters),
            new string('9', ScheduleContractLimits.Sha256HexCharacters),
            new string('8', ScheduleContractLimits.Sha256HexCharacters),
            result,
            occurrence.ScheduledAtUtc.AddSeconds(2));
    }

    private static ScheduleOccurrence Occurrence(ScheduleDefinition definition, int ordinal)
        => ScheduleEvaluatorTestData.Occurrence(
            ordinal,
            definition.Recurrence.FirstLocalOccurrence.AddDays(ordinal - 1),
            ScheduleEvaluatorTestData.FirstUtc.AddDays(ordinal - 1),
            definition.TimeZone);

    private static TriggerQueueEntry QueueEntry(
        string delivery = "delivery-1",
        string deduplication = "deduplication-1",
        DateTimeOffset? eligibleAtUtc = null,
        TriggerQueuePriority priority = TriggerQueuePriority.Normal)
    {
        Assert.True(TriggerDeliveryId.TryParse(delivery, out var deliveryId));
        Assert.True(TriggerDeduplicationId.TryParse(deduplication, out var deduplicationId));
        var recorded = _now.AddMinutes(-5);
        return new TriggerQueueEntry(
            deliveryId!,
            deduplicationId!,
            "loop-1",
            new string('a', 64),
            1,
            1,
            1,
            TriggerQueueEntryState.Queued,
            TriggerQueueTerminalReason.None,
            new TriggerQueueOrderKey(eligibleAtUtc ?? recorded, priority, recorded, deliveryId!.Value),
            1,
            recorded,
            null,
            TriggerAdmissionStatus.Admitted,
            TriggerAdmissionReason.EvidenceAccepted,
            WorkspaceId: _triggerWorkspaceId,
            TargetGraphId: "graph-1",
            TargetRevisionId: "revision-1");
    }

    private static GovernedLoopSleepCheckpoint Checkpoint()
    {
        var posture = GovernedLoopSleepApplicationTestFixture.Posture(observedAtUtc: _now);
        var request = GovernedLoopSleepApplicationTestFixture.PublicationRequest(posture, deadlineUtc: _now.AddMinutes(-1));
        return GovernedLoopSleepContractHash.Apply(new GovernedLoopSleepCheckpoint(
            GovernedLoopSleepCheckpoint.CurrentSchemaVersion,
            string.Empty,
            request.Binding,
            request.WakeMode,
            request.WakeDeadlineUtc,
            request.AuthenticatedEventReference,
            _now.AddMinutes(-2),
            string.Empty));
    }

    private static GovernedLoopCoordinatorReadResult Coordinator()
    {
        var ownership = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorOwnership(1, "background-coordinator", "owner-1", 1, _now.AddMinutes(-5), string.Empty));
        var lifecycle = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorLifecycle(1, 2, ownership, GovernedLoopCoordinatorStatus.Running, _now.AddMinutes(-4), null, string.Empty));
        var heartbeat = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorHeartbeat(1, 2, ownership, _now.AddMinutes(-1), _now.AddMinutes(4), string.Empty));
        return new GovernedLoopCoordinatorReadResult(GovernedLoopCoordinatorReadStatus.Found, new GovernedLoopCoordinatorSnapshot(ownership, lifecycle, heartbeat, 0, null));
    }
}
