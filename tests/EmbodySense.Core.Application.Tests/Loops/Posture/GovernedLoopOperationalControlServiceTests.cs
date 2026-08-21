using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Application.Tests.Triggers.Schedules;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Loops.Posture.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Application.Tests.Loops.Posture;

public sealed class GovernedLoopOperationalControlServiceTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-12T15:00:00Z");
    private static readonly string _workspaceId = "workspace-sha256:" + new string('1', 64);

    [Fact]
    public async Task Single_delivery_reserves_intent_before_mutation_and_terminal_replay_does_not_repeat_effect()
    {
        var entry = Entry("delivery-1", "deduplication-1", _now.AddMinutes(-2));
        var queue = new StubOperationalTriggerQueuePort { Snapshot = Snapshot(entry) };
        var receipts = new InMemoryReceiptStore();
        var sequence = new List<string>();
        receipts.OnBegin = () => sequence.Add("intent");
        queue.Cancellation = (_, _, _) =>
        {
            sequence.Add("mutation");
            Assert.Equal("intent", sequence[0]);
            return new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.Cancelled, Cancelled(entry));
        };
        var (service, authority) = await ServiceAsync(queue, receipts);
        var request = Request(GovernedLoopOperationalControlKind.CancelDelivery, entry.DeliveryId.Value, entry.Revision, entry.CanonicalEnvelopeHash, authority.EvidenceHash);

        var applied = await service.ExecuteAsync(request);
        var replayed = await service.ExecuteAsync(request);

        Assert.Equal(GovernedLoopOperationalControlStatus.Applied, applied.Status);
        Assert.Equal(GovernedLoopOperationalControlStatus.Replayed, replayed.Status);
        Assert.Equal(["intent", "mutation", "intent"], sequence);
        Assert.Single(queue.Cancellations);
        Assert.Equal(GovernedLoopOperationalControlReceiptState.Complete, receipts.Current!.State);
        Assert.Equal(GovernedLoopOperationalControlStatus.Applied, receipts.Current.Outcome);
    }

    [Fact]
    public async Task Bounded_batch_rejects_overflow_before_mutation_and_captures_an_immutable_identity_order()
    {
        var delivery2 = Entry("delivery-2", "deduplication-2", _now.AddMinutes(-3));
        var delivery1 = Entry("delivery-1", "deduplication-1", _now.AddMinutes(-2));
        var queue = new StubOperationalTriggerQueuePort { Snapshot = Snapshot(delivery2, delivery1) };
        queue.Cancellation = (id, _, _) =>
        {
            var current = queue.Snapshot.Entries.Single(item => item.DeliveryId.Equals(id));
            return new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.Cancelled, Cancelled(current));
        };
        var overflowReceipts = new InMemoryReceiptStore();
        var (overflowService, authority) = await ServiceAsync(queue, overflowReceipts);
        var catalogHash = GovernedLoopOperationalHash.QueueCatalog(4, 2, 2, 2, 2, false);
        var overflow = Request(GovernedLoopOperationalControlKind.CancelPendingDeliveries, "loop-1", 4, catalogHash, authority.EvidenceHash, maximumBatchItems: 1);

        var rejected = await overflowService.ExecuteAsync(overflow);

        Assert.Equal(GovernedLoopOperationalControlStatus.Backpressured, rejected.Status);
        Assert.Empty(queue.Cancellations);

        var receipts = new InMemoryReceiptStore();
        var (service, _) = await ServiceAsync(queue, receipts);
        var applied = await service.ExecuteAsync(overflow with { OperationId = "operation-batch", MaximumBatchItems = 2 });

        Assert.Equal(GovernedLoopOperationalControlStatus.Applied, applied.Status);
        Assert.Equal(["delivery-1", "delivery-2"], queue.Cancellations.Select(item => item.DeliveryId.Value));
        Assert.Equal(["delivery-1", "delivery-2"], receipts.Current!.Progress.Select(item => item.TargetId));
        Assert.All(receipts.Current.Progress, item => Assert.Equal(GovernedLoopOperationalControlStatus.Applied, item.Status));
    }

    [Theory]
    [InlineData(false, GovernedLoopOperationalControlStatus.Unavailable)]
    [InlineData(true, GovernedLoopOperationalControlStatus.Corrupt)]
    public async Task All_failed_batch_uses_explicit_fail_closed_precedence_instead_of_target_order(
        bool malformedMutation,
        GovernedLoopOperationalControlStatus expectedStatus)
    {
        var delivery1 = Entry("delivery-1", "deduplication-1", _now.AddMinutes(-3));
        var delivery2 = Entry("delivery-2", "deduplication-2", _now.AddMinutes(-2));
        var queue = new StubOperationalTriggerQueuePort { Snapshot = Snapshot(delivery1, delivery2) };
        queue.Cancellation = (id, _, _) => id.Equals(delivery1.DeliveryId)
            ? new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.NotFound, null)
            : malformedMutation
                ? new TriggerQueueCancellationResult((TriggerQueueCancellationStatus)999, null)
                : new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.Unavailable, null);
        var receipts = new InMemoryReceiptStore();
        var (service, authority) = await ServiceAsync(queue, receipts);
        var catalogHash = GovernedLoopOperationalHash.QueueCatalog(4, 2, 2, 2, 2, false);
        var request = Request(GovernedLoopOperationalControlKind.CancelPendingDeliveries, "loop-1", 4, catalogHash, authority.EvidenceHash, maximumBatchItems: 2);

        var result = await service.ExecuteAsync(request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal("delivery-batch-" + expectedStatus.ToString().ToLowerInvariant(), result.ReasonCode);
        Assert.Equal(GovernedLoopOperationalControlStatus.NotFound, receipts.Current!.Progress[0].Status);
        Assert.Equal(expectedStatus, receipts.Current.Progress[1].Status);
    }

    [Theory]
    [InlineData(GovernedLoopOperationalControlKind.PauseRun)]
    [InlineData(GovernedLoopOperationalControlKind.DisableSchedule)]
    [InlineData(GovernedLoopOperationalControlKind.CancelDelivery)]
    [InlineData(GovernedLoopOperationalControlKind.CancelPendingDeliveries)]
    public async Task Kind_invalid_target_is_rejected_before_any_durable_operation_reservation(GovernedLoopOperationalControlKind kind)
    {
        var receipts = new InMemoryReceiptStore();
        var (service, authority) = await ServiceAsync(new StubOperationalTriggerQueuePort { Snapshot = Snapshot() }, receipts);
        var request = Request(kind, "/", 1, new string('a', 64), authority.EvidenceHash, kind == GovernedLoopOperationalControlKind.CancelPendingDeliveries ? 2 : 1);

        var result = await service.ExecuteAsync(request);

        Assert.Equal(GovernedLoopOperationalControlStatus.Invalid, result.Status);
        Assert.Null(receipts.Current);
    }

    [Fact]
    public async Task Stale_authority_and_operation_collision_fail_closed_without_target_mutation()
    {
        var entry = Entry("delivery-1", "deduplication-1", _now.AddMinutes(-2));
        var queue = new StubOperationalTriggerQueuePort { Snapshot = Snapshot(entry) };
        var receipts = new InMemoryReceiptStore();
        var (service, authority) = await ServiceAsync(queue, receipts);
        var stale = Request(GovernedLoopOperationalControlKind.CancelDelivery, entry.DeliveryId.Value, 1, entry.CanonicalEnvelopeHash, new string('f', 64));

        Assert.Equal(GovernedLoopOperationalControlStatus.Unauthorized, (await service.ExecuteAsync(stale)).Status);
        Assert.Empty(queue.Cancellations);

        queue.Cancellation = (_, _, _) => new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.Cancelled, Cancelled(entry));
        var exact = Request(GovernedLoopOperationalControlKind.CancelDelivery, entry.DeliveryId.Value, 1, entry.CanonicalEnvelopeHash, authority.EvidenceHash);
        Assert.Equal(GovernedLoopOperationalControlStatus.Applied, (await service.ExecuteAsync(exact)).Status);
        Assert.Equal(GovernedLoopOperationalControlStatus.Conflict, (await service.ExecuteAsync(exact with { TargetId = "delivery-2" })).Status);
        Assert.Single(queue.Cancellations);
    }

    [Fact]
    public async Task Authority_change_after_durable_intent_fails_closed_before_target_mutation()
    {
        var entry = Entry("delivery-1", "deduplication-1", _now.AddMinutes(-2));
        var queue = new StubOperationalTriggerQueuePort { Snapshot = Snapshot(entry) };
        queue.Cancellation = (_, _, _) => new TriggerQueueCancellationResult(
            TriggerQueueCancellationStatus.Cancelled,
            Cancelled(entry));
        var receipts = new InMemoryReceiptStore();
        var (service, authorityPort, authority) = await ServiceWithAuthorityAsync(queue, receipts);
        receipts.OnBegin = () => authorityPort.Permitted = false;
        var request = Request(
            GovernedLoopOperationalControlKind.CancelDelivery,
            entry.DeliveryId.Value,
            entry.Revision,
            entry.CanonicalEnvelopeHash,
            authority.EvidenceHash);

        var result = await service.ExecuteAsync(request);

        Assert.Equal(GovernedLoopOperationalControlStatus.Unauthorized, result.Status);
        Assert.Equal("operational-control-authority-changed-before-effect", result.ReasonCode);
        Assert.Empty(queue.Cancellations);
        Assert.Equal(GovernedLoopOperationalControlReceiptState.Complete, receipts.Current!.State);
        Assert.Equal(GovernedLoopOperationalControlStatus.Unauthorized, receipts.Current.Outcome);
    }

    [Fact]
    public async Task Preexisting_same_shape_cancellation_after_intent_requires_review_without_claiming_the_effect()
    {
        var entry = Entry("delivery-1", "deduplication-1", _now.AddMinutes(-2));
        var queue = new StubOperationalTriggerQueuePort { Snapshot = Snapshot(entry) };
        var receipts = new InMemoryReceiptStore
        {
            OnBegin = () => queue.Snapshot = Snapshot(Cancelled(entry))
        };
        var (service, authority) = await ServiceAsync(queue, receipts);
        var request = Request(
            GovernedLoopOperationalControlKind.CancelDelivery,
            entry.DeliveryId.Value,
            entry.Revision,
            entry.CanonicalEnvelopeHash,
            authority.EvidenceHash);

        var result = await service.ExecuteAsync(request);

        Assert.Equal(GovernedLoopOperationalControlStatus.NeedsReview, result.Status);
        Assert.Equal("delivery-control-outcome-ambiguous", result.ReasonCode);
        Assert.Empty(queue.Cancellations);
        Assert.Equal(GovernedLoopOperationalControlReceiptState.NeedsReview, receipts.Current!.State);
        Assert.Equal(GovernedLoopOperationalControlStatus.NeedsReview, receipts.Current.Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Malformed_queue_mutation_evidence_fails_closed(int scenario)
    {
        var entry = Entry("delivery-1", "deduplication-1", _now.AddMinutes(-2));
        var queue = new StubOperationalTriggerQueuePort { Snapshot = Snapshot(entry) };
        queue.Cancellation = (_, _, _) => scenario switch
        {
            0 => new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.Cancelled, null),
            1 => new TriggerQueueCancellationResult(
                TriggerQueueCancellationStatus.Cancelled,
                Cancelled(Entry("delivery-2", "deduplication-2", _now.AddMinutes(-2)))),
            2 => new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.Cancelled, entry),
            3 => new TriggerQueueCancellationResult((TriggerQueueCancellationStatus)999, null),
            _ => new TriggerQueueCancellationResult(
                TriggerQueueCancellationStatus.Cancelled,
                Cancelled(entry) with { LoopId = new string('x', 10_000) })
        };
        var receipts = new InMemoryReceiptStore();
        var (service, authority) = await ServiceAsync(queue, receipts);
        var request = Request(
            GovernedLoopOperationalControlKind.CancelDelivery,
            entry.DeliveryId.Value,
            entry.Revision,
            entry.CanonicalEnvelopeHash,
            authority.EvidenceHash);

        var result = await service.ExecuteAsync(request);

        Assert.Equal(GovernedLoopOperationalControlStatus.Corrupt, result.Status);
        Assert.Equal("delivery-control-mutation-evidence-corrupt", result.ReasonCode);
        Assert.Single(queue.Cancellations);
        Assert.Equal(GovernedLoopOperationalControlStatus.Corrupt, receipts.Current!.Outcome);
    }

    [Fact]
    public async Task Fresh_schedule_compare_exchange_applies_once_and_terminal_replay_does_not_repeat_it()
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        var state = ScheduleEvaluatorTestData.State(definition);
        Assert.True(ScheduleContractHash.TryComputeState(state, out var stateHash, out _));
        var schedules = new TestScheduleStore(definition, state);
        var receipts = new InMemoryReceiptStore();
        var (service, authority) = await ServiceAsync(new StubOperationalTriggerQueuePort { Snapshot = Snapshot() }, receipts, schedules);
        var request = Request(
            GovernedLoopOperationalControlKind.DisableSchedule,
            definition.ScheduleId.Value,
            state.StateRevision,
            stateHash!,
            authority.EvidenceHash);

        var applied = await service.ExecuteAsync(request);
        var replayed = await service.ExecuteAsync(request);

        Assert.Equal(GovernedLoopOperationalControlStatus.Applied, applied.Status);
        Assert.Equal(GovernedLoopOperationalControlStatus.Replayed, replayed.Status);
        Assert.False(schedules.State.Enabled);
        Assert.Single(schedules.Mutations);
    }

    [Fact]
    public async Task Pending_schedule_retry_never_infers_that_an_unrelated_same_shape_successor_was_its_effect()
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        var expected = ScheduleEvaluatorTestData.State(definition);
        var unrelated = expected with { StateRevision = expected.StateRevision + 1, Enabled = false };
        Assert.True(ScheduleContractHash.TryComputeState(expected, out var expectedHash, out _));
        var schedules = new TestScheduleStore(definition, unrelated);
        var receipts = new InMemoryReceiptStore();
        var (service, authority) = await ServiceAsync(new StubOperationalTriggerQueuePort { Snapshot = Snapshot() }, receipts, schedules);
        var request = Request(
            GovernedLoopOperationalControlKind.DisableSchedule,
            definition.ScheduleId.Value,
            expected.StateRevision,
            expectedHash!,
            authority.EvidenceHash);
        receipts.Seed(PendingReceipt(request, authority));

        var result = await service.ExecuteAsync(request);

        Assert.Equal(GovernedLoopOperationalControlStatus.NeedsReview, result.Status);
        Assert.Equal("schedule-control-outcome-ambiguous", result.ReasonCode);
        Assert.Empty(schedules.Mutations);
        Assert.Equal(GovernedLoopOperationalControlReceiptState.NeedsReview, receipts.Current!.State);
        Assert.Equal(GovernedLoopOperationalControlStatus.NeedsReview, receipts.Current.Outcome);
    }

    [Fact]
    public async Task Pending_schedule_retry_safely_applies_when_the_expected_state_is_still_current()
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        var expected = ScheduleEvaluatorTestData.State(definition);
        Assert.True(ScheduleContractHash.TryComputeState(expected, out var expectedHash, out _));
        var schedules = new TestScheduleStore(definition, expected);
        var receipts = new InMemoryReceiptStore();
        var (service, authority) = await ServiceAsync(new StubOperationalTriggerQueuePort { Snapshot = Snapshot() }, receipts, schedules);
        var request = Request(
            GovernedLoopOperationalControlKind.DisableSchedule,
            definition.ScheduleId.Value,
            expected.StateRevision,
            expectedHash!,
            authority.EvidenceHash);
        receipts.Seed(PendingReceipt(request, authority));

        var result = await service.ExecuteAsync(request);

        Assert.Equal(GovernedLoopOperationalControlStatus.Applied, result.Status);
        Assert.Single(schedules.Mutations);
        Assert.False(schedules.State.Enabled);
    }

    [Fact]
    public async Task Same_shape_writer_winning_between_read_and_compare_exchange_requires_review()
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        var expected = ScheduleEvaluatorTestData.State(definition);
        Assert.True(ScheduleContractHash.TryComputeState(expected, out var expectedHash, out _));
        var schedules = new TestScheduleStore(definition, expected) { ReturnExactReplay = true };
        var receipts = new InMemoryReceiptStore();
        var (service, authority) = await ServiceAsync(new StubOperationalTriggerQueuePort { Snapshot = Snapshot() }, receipts, schedules);
        var request = Request(
            GovernedLoopOperationalControlKind.DisableSchedule,
            definition.ScheduleId.Value,
            expected.StateRevision,
            expectedHash!,
            authority.EvidenceHash);

        var result = await service.ExecuteAsync(request);

        Assert.Equal(GovernedLoopOperationalControlStatus.NeedsReview, result.Status);
        Assert.Equal("schedule-control-outcome-ambiguous", result.ReasonCode);
        Assert.Single(schedules.Mutations);
        Assert.Equal(GovernedLoopOperationalControlReceiptState.NeedsReview, receipts.Current!.State);
    }

    [Fact]
    public async Task Immutable_disabled_schedule_rejects_enable_before_constructing_a_replacement()
    {
        var definition = ScheduleEvaluatorTestData.Definition(enabled: false);
        var state = ScheduleEvaluatorTestData.State(definition);
        Assert.True(ScheduleContractHash.TryComputeState(state, out var stateHash, out _));
        var schedules = new TestScheduleStore(definition, state);
        var receipts = new InMemoryReceiptStore();
        var (service, authority) = await ServiceAsync(new StubOperationalTriggerQueuePort { Snapshot = Snapshot() }, receipts, schedules);
        var request = Request(
            GovernedLoopOperationalControlKind.EnableSchedule,
            definition.ScheduleId.Value,
            state.StateRevision,
            stateHash!,
            authority.EvidenceHash);

        var result = await service.ExecuteAsync(request);

        Assert.Equal(GovernedLoopOperationalControlStatus.Ineligible, result.Status);
        Assert.Equal("schedule-control-definition-disabled", result.ReasonCode);
        Assert.Empty(schedules.Mutations);
        Assert.False(schedules.State.Enabled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Malformed_schedule_mutation_evidence_fails_closed(int scenario)
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        var state = ScheduleEvaluatorTestData.State(definition);
        Assert.True(ScheduleContractHash.TryComputeState(state, out var stateHash, out _));
        var schedules = new TestScheduleStore(definition, state)
        {
            ReturnNullMutation = scenario == 0,
            ReturnAppliedWithoutCurrentState = scenario == 1,
            NextMutationStatus = scenario switch
            {
                2 => ScheduleStoreMutationStatus.Applied,
                3 => ScheduleStoreMutationStatus.AlreadyExists,
                _ => null
            }
        };
        if (scenario == 4)
        {
            schedules.MutationStatusSelector = _ =>
            {
                schedules.State = state with { DefinitionHash = "not-a-sha256-hash" };
                return ScheduleStoreMutationStatus.Applied;
            };
        }
        var receipts = new InMemoryReceiptStore();
        var (service, authority) = await ServiceAsync(new StubOperationalTriggerQueuePort { Snapshot = Snapshot() }, receipts, schedules);
        var request = Request(
            GovernedLoopOperationalControlKind.DisableSchedule,
            definition.ScheduleId.Value,
            state.StateRevision,
            stateHash!,
            authority.EvidenceHash);

        var result = await service.ExecuteAsync(request);

        Assert.Equal(GovernedLoopOperationalControlStatus.Corrupt, result.Status);
        Assert.Equal("schedule-control-mutation-evidence-corrupt", result.ReasonCode);
        Assert.Single(schedules.Mutations);
        Assert.Equal(GovernedLoopOperationalControlStatus.Corrupt, receipts.Current!.Outcome);
    }

    [Theory]
    [InlineData(ScheduleStoreMutationStatus.Unavailable, 0, GovernedLoopOperationalControlStatus.Unavailable)]
    [InlineData(ScheduleStoreMutationStatus.Unavailable, 1, GovernedLoopOperationalControlStatus.Unavailable)]
    [InlineData(ScheduleStoreMutationStatus.Unavailable, 2, GovernedLoopOperationalControlStatus.Corrupt)]
    [InlineData(ScheduleStoreMutationStatus.Backpressured, 0, GovernedLoopOperationalControlStatus.Backpressured)]
    [InlineData(ScheduleStoreMutationStatus.Backpressured, 1, GovernedLoopOperationalControlStatus.Backpressured)]
    [InlineData(ScheduleStoreMutationStatus.Backpressured, 2, GovernedLoopOperationalControlStatus.Corrupt)]
    public async Task Schedule_failure_mutation_evidence_accepts_optional_valid_state_and_rejects_malformed_state(ScheduleStoreMutationStatus mutationStatus, int evidenceShape, GovernedLoopOperationalControlStatus expectedStatus)
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        var state = ScheduleEvaluatorTestData.State(definition);
        Assert.True(ScheduleContractHash.TryComputeState(state, out var stateHash, out _));
        var schedules = new TestScheduleStore(definition, state)
        {
            NextMutationStatus = mutationStatus,
            ReturnNextMutationWithoutCurrentState = evidenceShape == 0
        };
        if (evidenceShape == 2)
        {
            schedules.MutationStatusSelector = _ =>
            {
                schedules.State = state with { DefinitionHash = "not-a-sha256-hash" };
                return mutationStatus;
            };
        }
        var receipts = new InMemoryReceiptStore();
        var (service, authority) = await ServiceAsync(new StubOperationalTriggerQueuePort { Snapshot = Snapshot() }, receipts, schedules);
        var request = Request(
            GovernedLoopOperationalControlKind.DisableSchedule,
            definition.ScheduleId.Value,
            state.StateRevision,
            stateHash!,
            authority.EvidenceHash);

        var result = await service.ExecuteAsync(request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(
            evidenceShape == 2
                ? "schedule-control-mutation-evidence-corrupt"
                : "schedule-control-" + mutationStatus.ToString().ToLowerInvariant(),
            result.ReasonCode);
        Assert.Single(schedules.Mutations);
        Assert.Equal(expectedStatus, receipts.Current!.Outcome);
    }

    [Theory]
    [InlineData(GovernedLoopOperationalControlKind.PauseRun, CustomLoopRunStatus.Running, CustomLoopControlStatus.Paused, "pause")]
    [InlineData(GovernedLoopOperationalControlKind.CancelRun, CustomLoopRunStatus.Admitted, CustomLoopControlStatus.Cancelled, "cancel")]
    [InlineData(GovernedLoopOperationalControlKind.ResumeRun, CustomLoopRunStatus.Paused, CustomLoopControlStatus.Resumed, "resume")]
    public async Task Fresh_run_control_revalidates_the_current_monitor_before_delegating_the_exact_lifecycle_operation(
        GovernedLoopOperationalControlKind kind,
        CustomLoopRunStatus runStatus,
        CustomLoopControlStatus lifecycleStatus,
        string expectedOperation)
    {
        var artifactHash = new string('b', 64);
        var runStore = new StubRunStore { Monitor = RunMonitor(runStatus, 2, artifactHash) };
        var lifecycle = new StubLifecycle { ResultStatus = lifecycleStatus };
        var receipts = new InMemoryReceiptStore();
        var (service, authority) = await ServiceAsync(
            new StubOperationalTriggerQueuePort { Snapshot = Snapshot() },
            receipts,
            runs: runStore,
            lifecycle: lifecycle);
        var request = Request(kind, "run-1", 2, artifactHash, authority.EvidenceHash);

        var result = await service.ExecuteAsync(request);

        Assert.Equal(GovernedLoopOperationalControlStatus.Applied, result.Status);
        Assert.Equal("run-control-" + lifecycleStatus.ToString().ToLowerInvariant(), result.ReasonCode);
        Assert.Equal(expectedOperation, lifecycle.LastOperation);
        Assert.Null(result.CurrentRevision);
        Assert.Equal(GovernedLoopOperationalControlReceiptState.Complete, receipts.Current!.State);
    }

    [Theory]
    [InlineData(0, GovernedLoopOperationalControlStatus.NotFound, "run-control-not-found")]
    [InlineData(1, GovernedLoopOperationalControlStatus.Corrupt, "run-control-evidence-corrupt")]
    [InlineData(2, GovernedLoopOperationalControlStatus.Conflict, "run-control-revision-conflict")]
    [InlineData(3, GovernedLoopOperationalControlStatus.Ineligible, "run-control-lifecycle-ineligible")]
    public async Task Fresh_run_control_fails_closed_when_current_monitor_evidence_cannot_admit_the_requested_effect(
        int scenario,
        GovernedLoopOperationalControlStatus expectedStatus,
        string expectedReason)
    {
        var artifactHash = new string('b', 64);
        var runStore = new StubRunStore
        {
            Monitor = scenario switch
            {
                0 => null,
                1 => RunMonitor(CustomLoopRunStatus.Running, 2, "not-a-sha256-hash"),
                2 => RunMonitor(CustomLoopRunStatus.Running, 3, artifactHash),
                _ => RunMonitor(CustomLoopRunStatus.Completed, 2, artifactHash)
            }
        };
        var lifecycle = new StubLifecycle();
        var receipts = new InMemoryReceiptStore();
        var (service, authority) = await ServiceAsync(
            new StubOperationalTriggerQueuePort { Snapshot = Snapshot() },
            receipts,
            runs: runStore,
            lifecycle: lifecycle);
        var request = Request(GovernedLoopOperationalControlKind.PauseRun, "run-1", 2, artifactHash, authority.EvidenceHash);

        var result = await service.ExecuteAsync(request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Null(lifecycle.LastOperation);
        Assert.Equal(expectedStatus, receipts.Current!.Outcome);
    }

    private static async Task<(GovernedLoopOperationalControlService Service, GovernedLoopOperationalControlAuthority Authority)> ServiceAsync(
        StubOperationalTriggerQueuePort queue,
        InMemoryReceiptStore receipts,
        IScheduleStorePort? schedules = null,
        ICustomLoopRunStore? runs = null,
        ICustomLoopLifecycleControlPort? lifecycle = null)
    {
        var (service, _, authority) = await ServiceWithAuthorityAsync(queue, receipts, schedules, runs, lifecycle);
        return (service, authority);
    }

    private static async Task<(GovernedLoopOperationalControlService Service, StubOperationalControlAuthorityPort AuthorityPort, GovernedLoopOperationalControlAuthority Authority)> ServiceWithAuthorityAsync(
        StubOperationalTriggerQueuePort queue,
        InMemoryReceiptStore receipts,
        IScheduleStorePort? schedules = null,
        ICustomLoopRunStore? runs = null,
        ICustomLoopLifecycleControlPort? lifecycle = null)
    {
        var authority = new StubOperationalControlAuthorityPort { WorkspaceId = _workspaceId, ObservedAtUtc = _now };
        return (
            new GovernedLoopOperationalControlService(
                authority,
                receipts,
                queue,
                queue,
                schedules ?? new StubScheduleStore(),
                runs ?? new StubRunStore(),
                lifecycle ?? new StubLifecycle(),
                new FixedTimeProvider(_now)),
            authority,
            (await authority.ReadCurrentAsync())!);
    }

    private static CustomLoopRunMonitor RunMonitor(CustomLoopRunStatus status, int lifecycleVersion, string artifactHash)
        => new(
            new CustomLoopRunSummary(
                "run-1",
                "loop-1",
                "admission-1",
                1,
                lifecycleVersion,
                status,
                _now,
                _now,
                null,
                0,
                0,
                null,
                IsDeleted: false),
            artifactHash);

    private static GovernedLoopOperationalControlRequest Request(
        GovernedLoopOperationalControlKind kind,
        string targetId,
        long expectedRevision,
        string expectedHash,
        string authorityHash,
        int maximumBatchItems = 1)
        => new(
            GovernedLoopOperationalControlRequest.CurrentSchemaVersion,
            _workspaceId,
            "operation-1",
            kind,
            targetId,
            expectedRevision,
            expectedHash,
            authorityHash,
            "actor-1",
            "startup",
            maximumBatchItems);

    private static GovernedLoopOperationalControlReceipt PendingReceipt(
        GovernedLoopOperationalControlRequest request,
        GovernedLoopOperationalControlAuthority authority)
        => GovernedLoopOperationalControlReceiptFactory.Create(
            request,
            GovernedLoopOperationalHash.Request(request),
            authority,
            _now,
            _now,
            GovernedLoopOperationalControlReceiptState.Pending,
            GovernedLoopOperationalControlStatus.OperationInProgress,
            "operational-control-pending",
            []);

    private static TriggerQueueEntry Entry(string delivery, string deduplication, DateTimeOffset recordedAtUtc)
    {
        Assert.True(TriggerDeliveryId.TryParse(delivery, out var deliveryId));
        Assert.True(TriggerDeduplicationId.TryParse(deduplication, out var deduplicationId));
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
            new TriggerQueueOrderKey(recordedAtUtc, TriggerQueuePriority.Normal, recordedAtUtc, delivery),
            1,
            recordedAtUtc,
            null,
            TriggerAdmissionStatus.Admitted,
            TriggerAdmissionReason.EvidenceAccepted,
            WorkspaceId: new string('1', 64));
    }

    private static TriggerQueueEntry Cancelled(TriggerQueueEntry entry)
        => entry with
        {
            QueuedReservationBytes = 0,
            State = TriggerQueueEntryState.Cancelled,
            TerminalReason = TriggerQueueTerminalReason.Cancelled,
            Revision = entry.Revision + 1,
            TerminalAtUtc = _now
        };

    private static TriggerQueueSnapshot Snapshot(params TriggerQueueEntry[] entries)
    {
        var nonterminal = entries
            .Where(item => item.State is TriggerQueueEntryState.Queued or TriggerQueueEntryState.WorkerOwned or TriggerQueueEntryState.Dispatching)
            .ToArray();
        return new TriggerQueueSnapshot(
            TriggerQueueSnapshot.CurrentSchemaVersion,
            4,
            TriggerQueueQuota.Default,
            nonterminal.Length,
            nonterminal.Sum(item => (long)item.SerializedEntryBytes),
            nonterminal.Sum(item => (long)item.QueuedReservationBytes),
            entries.Length,
            entries.Sum(item => (long)item.SerializedEntryBytes),
            entries.Sum(item => (long)item.RetainedReservationBytes),
            0,
            false,
            entries);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class InMemoryReceiptStore : IGovernedLoopOperationalControlReceiptStore
    {
        internal Action? OnBegin { get; set; }
        internal GovernedLoopOperationalControlReceipt? Current { get; private set; }

        internal void Seed(GovernedLoopOperationalControlReceipt receipt) => Current = receipt;

        public Task<GovernedLoopOperationalControlReceiptStoreResult> BeginAsync(GovernedLoopOperationalControlReceipt receipt, CancellationToken cancellationToken = default)
        {
            OnBegin?.Invoke();
            if (Current is null)
            {
                Current = receipt;
                return Task.FromResult(new GovernedLoopOperationalControlReceiptStoreResult(GovernedLoopOperationalControlReceiptStoreStatus.Committed, receipt, new Lease()));
            }
            if (!string.Equals(Current.RequestHash, receipt.RequestHash, StringComparison.Ordinal))
            {
                return Task.FromResult(new GovernedLoopOperationalControlReceiptStoreResult(GovernedLoopOperationalControlReceiptStoreStatus.Conflict, Current));
            }
            return Task.FromResult(new GovernedLoopOperationalControlReceiptStoreResult(GovernedLoopOperationalControlReceiptStoreStatus.Replayed, Current, Current.State is GovernedLoopOperationalControlReceiptState.Complete or GovernedLoopOperationalControlReceiptState.NeedsReview ? null : new Lease()));
        }

        public Task<GovernedLoopOperationalControlReceiptStoreResult> CompareExchangeAsync(string expectedContentHash, GovernedLoopOperationalControlReceipt replacement, CancellationToken cancellationToken = default)
        {
            if (Current is null || !string.Equals(Current.ContentHash, expectedContentHash, StringComparison.Ordinal))
            {
                return Task.FromResult(new GovernedLoopOperationalControlReceiptStoreResult(GovernedLoopOperationalControlReceiptStoreStatus.Conflict, Current));
            }
            Current = replacement;
            return Task.FromResult(new GovernedLoopOperationalControlReceiptStoreResult(GovernedLoopOperationalControlReceiptStoreStatus.Committed, replacement));
        }

        private sealed class Lease : IGovernedLoopOperationalControlLease
        {
            public void Dispose() { }
        }
    }

    private sealed class StubScheduleStore : IScheduleStorePort
    {
        public Task<ScheduleStoreReadResult> ReadAsync(ScheduleId scheduleId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ScheduleStoreReadResult(ScheduleStoreReadStatus.NotFound, null, null));
        public Task<ScheduleStoreMutationResult> CreateAsync(ScheduleStoreCreateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScheduleStoreMutationResult> CompareExchangeAsync(ScheduleStateCompareExchange request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubLifecycle : ICustomLoopLifecycleControlPort
    {
        internal CustomLoopControlStatus ResultStatus { get; init; } = CustomLoopControlStatus.Paused;
        internal string? LastOperation { get; private set; }

        public Task<CustomLoopControlResult> PauseAsync(CustomLoopPauseRequest request, CancellationToken cancellationToken = default)
        {
            LastOperation = "pause";
            return Task.FromResult(new CustomLoopControlResult(ResultStatus, null, request.OperationId, "test"));
        }

        public Task<CustomLoopControlResult> CancelAsync(CustomLoopCancelRequest request, CancellationToken cancellationToken = default)
        {
            LastOperation = "cancel";
            return Task.FromResult(new CustomLoopControlResult(ResultStatus, null, request.OperationId, "test"));
        }

        public Task<CustomLoopControlResult> ResumeAsync(CustomLoopResumeRequest request, CancellationToken cancellationToken = default)
        {
            LastOperation = "resume";
            return Task.FromResult(new CustomLoopControlResult(ResultStatus, null, request.OperationId, "test"));
        }
    }

    private sealed class StubRunStore : ICustomLoopRunStore
    {
        internal CustomLoopRunMonitor? Monitor { get; init; }

        public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(null);
        public Task<CustomLoopRunMonitor?> GetMonitorAsync(string runId, CancellationToken cancellationToken = default) => Task.FromResult(Monitor);
        public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(null);
        public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(null);
        public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomLoopRunSummary>>([]);
        public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomLoopRunRecord>>([]);
        public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
