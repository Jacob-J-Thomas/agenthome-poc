using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Tests.Triggers.Schedules;

public sealed class ScheduleMalformedPortEvidenceTests
{
    [Fact]
    public void Public_recurrence_proof_hash_rejects_unbounded_resolution_strings()
    {
        var occurrence = ScheduleEvaluatorTestData.Occurrence();
        var oversized = new ScheduleTimeZoneResolution(
            ScheduleTimeZoneResolutionStatus.Unique,
            new string('f', 1_000_000),
            occurrence.ScheduledLocal,
            occurrence.ScheduledAtUtc,
            null);

        Assert.Throws<ArgumentException>(() => ScheduleRecurrenceProofHash.ComputeLocalResolution(
            occurrence.TimeZone,
            occurrence.ScheduledLocal,
            oversized));
    }

    [Fact]
    public async Task Null_and_oversized_time_zone_results_fail_closed_before_proof_hashing()
    {
        var nullResult = Fixture(timeZone: new NullTimeZone());
        var nullOutcome = await nullResult.Evaluator.EvaluateAsync(nullResult.Definition.ScheduleId);

        var oversizedResult = Fixture(timeZone: new OversizedTimeZone());
        var oversizedOutcome = await oversizedResult.Evaluator.EvaluateAsync(oversizedResult.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, nullOutcome.Status);
        Assert.Equal("time-zone-evidence-invalid", nullOutcome.ReasonCode);
        Assert.Equal(ScheduleEvaluationStatus.Corrupt, oversizedOutcome.Status);
        Assert.Equal("time-zone-evidence-invalid", oversizedOutcome.ReasonCode);
    }

    [Fact]
    public async Task Null_and_oversized_fixed_interval_results_fail_closed_before_proof_hashing()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            intervalSeconds: 60);
        var nullResult = Fixture(definition, timeZone: new NullTimeZone());
        var nullOutcome = await nullResult.Evaluator.EvaluateAsync(nullResult.Definition.ScheduleId);

        var oversizedResult = Fixture(definition, timeZone: new OversizedTimeZone());
        var oversizedOutcome = await oversizedResult.Evaluator.EvaluateAsync(oversizedResult.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, nullOutcome.Status);
        Assert.Equal("time-zone-evidence-invalid", nullOutcome.ReasonCode);
        Assert.Equal(ScheduleEvaluationStatus.Corrupt, oversizedOutcome.Status);
        Assert.Equal("time-zone-evidence-invalid", oversizedOutcome.ReasonCode);
    }

    [Fact]
    public async Task Null_overlap_and_current_evidence_results_fail_closed_without_escaping()
    {
        var nullOverlap = Fixture(overlap: new NullOverlap());
        var overlapOutcome = await nullOverlap.Evaluator.EvaluateAsync(nullOverlap.Definition.ScheduleId);

        var nullCurrent = Fixture(currentEvidence: new NullCurrentEvidence());
        var currentOutcome = await nullCurrent.Evaluator.EvaluateAsync(nullCurrent.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, overlapOutcome.Status);
        Assert.Equal("overlap-evidence-invalid", overlapOutcome.ReasonCode);
        Assert.Equal(ScheduleEvaluationStatus.Corrupt, currentOutcome.Status);
        Assert.Equal("schedule-evidence-corrupt", currentOutcome.ReasonCode);
    }

    [Fact]
    public async Task Null_store_read_fails_closed_before_any_mutation()
    {
        var fixture = Fixture();
        fixture.Store.ReturnNullRead = true;

        var outcome = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, outcome.Status);
        Assert.Equal("schedule-store-evidence-invalid", outcome.ReasonCode);
        Assert.Null(outcome.State);
        Assert.Empty(fixture.Store.Mutations);
    }

    [Fact]
    public async Task Store_read_for_a_different_schedule_identity_fails_closed_before_mutation()
    {
        var fixture = Fixture();
        Assert.True(ScheduleId.TryParse("different-schedule", out var differentScheduleId));

        var outcome = await fixture.Evaluator.EvaluateAsync(differentScheduleId!);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, outcome.Status);
        Assert.Equal("schedule-store-evidence-invalid", outcome.ReasonCode);
        Assert.Null(outcome.State);
        Assert.Empty(fixture.Store.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Null_or_applied_without_state_mutation_fails_closed_before_queueing(bool appliedWithoutState)
    {
        var queue = new TestScheduleQueue();
        var fixture = Fixture(queue: queue);
        fixture.Store.ReturnAppliedWithoutCurrentState = appliedWithoutState;
        fixture.Store.ReturnNullMutation = !appliedWithoutState;

        var outcome = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, outcome.Status);
        Assert.Equal("schedule-store-evidence-invalid", outcome.ReasonCode);
        Assert.Single(fixture.Store.Mutations);
        Assert.Equal(0, queue.Calls);
    }

    [Fact]
    public async Task Limit_plus_one_payload_is_rejected_without_a_second_unbounded_copy()
    {
        var fixture = Fixture(currentEvidence: new OversizedPayloadEvidence());

        var outcome = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, outcome.Status);
        Assert.Equal("schedule-evidence-corrupt", outcome.ReasonCode);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, outcome.State!.PendingDelivery!.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_post_effect_queue_result_is_durably_ambiguous(bool invalidEnumeration)
    {
        var queue = new MalformedQueue(invalidEnumeration);
        var fixture = Fixture(queue: queue);

        var outcome = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(1, queue.Calls);
        Assert.Equal(ScheduleEvaluationStatus.NeedsReview, outcome.Status);
        Assert.Equal("queue-evidence-conflict", outcome.ReasonCode);
        Assert.Equal(SchedulePendingDeliveryPhase.ResultObserved, outcome.State!.PendingDelivery!.Phase);
        Assert.Equal(ScheduleDeliveryResultKind.Ambiguous, outcome.State.PendingDelivery.Result!.Kind);
        Assert.Equal(outcome.State, fixture.Store.State);
    }

    [Fact]
    public async Task Contradictory_queue_and_admission_outcomes_are_durably_ambiguous()
    {
        var queue = new ContradictoryQueue();
        var fixture = Fixture(queue: queue);

        var outcome = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(1, queue.Calls);
        Assert.Equal(ScheduleEvaluationStatus.NeedsReview, outcome.Status);
        Assert.Equal("queue-evidence-conflict", outcome.ReasonCode);
        Assert.Equal(SchedulePendingDeliveryPhase.ResultObserved, outcome.State!.PendingDelivery!.Phase);
        Assert.Equal(ScheduleDeliveryResultKind.Ambiguous, outcome.State.PendingDelivery.Result!.Kind);
        Assert.Equal(outcome.State, fixture.Store.State);
    }

    [Fact]
    public async Task Missing_queue_admission_evidence_is_durably_ambiguous()
    {
        var queue = new MissingAdmissionQueue();
        var fixture = Fixture(queue: queue);

        var outcome = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(1, queue.Calls);
        Assert.Equal(ScheduleEvaluationStatus.NeedsReview, outcome.Status);
        Assert.Equal("queue-evidence-conflict", outcome.ReasonCode);
        Assert.Equal(ScheduleDeliveryResultKind.Ambiguous, outcome.State!.PendingDelivery!.Result!.Kind);
    }

    [Fact]
    public void Recurrence_proof_hash_binds_overlap_decision_evidence()
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out _));
        var occurrence = ScheduleEvaluatorTestData.Occurrence(timeZone: definition.TimeZone);
        var successor = ScheduleEvaluatorTestData.Occurrence(
            ordinal: 2,
            local: occurrence.ScheduledLocal.AddDays(1),
            utc: occurrence.ScheduledAtUtc.AddDays(1),
            timeZone: definition.TimeZone);
        var recordedAtUtc = occurrence.ScheduledAtUtc.AddHours(1);
        var evidence = new ScheduleOccurrenceDispositionEvidence(
            ScheduleOccurrenceDispositionEvidence.CurrentSchemaVersion,
            occurrence.Ordinal,
            occurrence.Ordinal,
            1,
            occurrence.ScheduledLocal,
            occurrence.ScheduledLocal,
            occurrence.ScheduledAtUtc,
            occurrence.ScheduledAtUtc,
            occurrence.TimeZone,
            ScheduleOccurrenceDisposition.OverlapSkipped,
            new string('a', ScheduleContractLimits.Sha256HexCharacters),
            "overlap-policy-skip",
            recordedAtUtc);
        var firstPlan = new ScheduleFinalizationPlan(1, successor, null, null, [evidence]);
        var secondPlan = firstPlan with
        {
            DispositionEvidence = [evidence with { DecisionEvidenceHash = new string('b', ScheduleContractLimits.Sha256HexCharacters) }],
        };

        var firstHash = ScheduleRecurrenceProofHash.Compute(definitionHash!, occurrence, firstPlan, []);
        var secondHash = ScheduleRecurrenceProofHash.Compute(definitionHash!, occurrence, secondPlan, []);

        Assert.NotEqual(firstHash, secondHash);
    }

    private static FixtureContext Fixture(
        ScheduleDefinition? definition = null,
        IScheduleCurrentEvidencePort? currentEvidence = null,
        IScheduleOverlapPort? overlap = null,
        IScheduleTimeZonePort? timeZone = null,
        ITriggerQueueAdmissionPort? queue = null)
    {
        definition ??= ScheduleEvaluatorTestData.Definition();
        var store = new TestScheduleStore(definition, ScheduleEvaluatorTestData.State(definition));
        currentEvidence ??= new TestScheduleCurrentEvidence();
        overlap ??= new TestScheduleOverlap();
        timeZone ??= new TestScheduleTimeZone();
        queue ??= new TestScheduleQueue();
        var clock = new TestScheduleTimeProvider(ScheduleEvaluatorTestData.Now);
        return new FixtureContext(
            definition,
            store,
            new ScheduleDueOccurrenceEvaluator(store, currentEvidence, overlap, timeZone, queue, clock));
    }

    private sealed record FixtureContext(
        ScheduleDefinition Definition,
        TestScheduleStore Store,
        ScheduleDueOccurrenceEvaluator Evaluator);

    private sealed class NullTimeZone : IScheduleTimeZonePort
    {
        public Task<ScheduleTimeZoneResolution> ResolveLocalAsync(
            ScheduleTimeZoneReference timeZone,
            DateTime scheduledLocal,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ScheduleTimeZoneResolution>(null!);

        public Task<ScheduleInstantResolution> ResolveInstantAsync(
            ScheduleTimeZoneReference timeZone,
            DateTimeOffset scheduledAtUtc,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ScheduleInstantResolution>(null!);
    }

    private sealed class OversizedTimeZone : IScheduleTimeZonePort
    {
        public Task<ScheduleTimeZoneResolution> ResolveLocalAsync(
            ScheduleTimeZoneReference timeZone,
            DateTime scheduledLocal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                new string('f', 1_000_000),
                scheduledLocal,
                new DateTimeOffset(scheduledLocal.AddHours(5), TimeSpan.Zero),
                null));

        public Task<ScheduleInstantResolution> ResolveInstantAsync(
            ScheduleTimeZoneReference timeZone,
            DateTimeOffset scheduledAtUtc,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ScheduleInstantResolution(
                ScheduleInstantResolutionStatus.Resolved,
                new string('f', 1_000_000),
                DateTime.SpecifyKind(scheduledAtUtc.UtcDateTime.AddHours(-5), DateTimeKind.Unspecified)));
    }

    private sealed class NullOverlap : IScheduleOverlapPort
    {
        public Task<ScheduleOverlapResult> GetStatusAsync(
            TriggerLoopReference target,
            ScheduleOccurrenceIdentity occurrenceIdentity,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ScheduleOverlapResult>(null!);
    }

    private sealed class NullCurrentEvidence : IScheduleCurrentEvidencePort
    {
        public Task<ScheduleCurrentEvidenceResult> ResolveAsync(
            ScheduleDefinition definition,
            ScheduleOccurrence occurrence,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ScheduleCurrentEvidenceResult>(null!);
    }

    private sealed class OversizedPayloadEvidence : IScheduleCurrentEvidencePort
    {
        public Task<ScheduleCurrentEvidenceResult> ResolveAsync(
            ScheduleDefinition definition,
            ScheduleOccurrence occurrence,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default)
        {
            var valid = ScheduleEvaluatorTestData.Evidence(definition, observedAtUtc);
            var malformed = new ScheduleCurrentEvidence(
                valid.EvidenceHash,
                valid.ObservedAtUtc,
                valid.Target,
                valid.Adapter,
                valid.ActorContext,
                valid.Authority,
                valid.RecurrencePermitted,
                new byte[TriggerDeliveryLimits.MaxInlinePayloadBytes + 1]);
            return Task.FromResult(new ScheduleCurrentEvidenceResult(
                ScheduleCurrentEvidenceStatus.Available,
                malformed));
        }
    }

    private sealed class MalformedQueue(bool invalidEnumeration) : ITriggerQueueAdmissionPort
    {
        public int Calls { get; private set; }

        public Task<TriggerQueueAdmissionResult> AdmitAsync(
            TriggerQueueAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (!invalidEnumeration)
            {
                return Task.FromResult<TriggerQueueAdmissionResult>(null!);
            }

            var envelope = request.DeliveryRequest.Envelope;
            Assert.True(TriggerDeliveryHash.TryCompute(envelope, out var hash, out _));
            return Task.FromResult(new TriggerQueueAdmissionResult(
                (TriggerQueueAdmissionStatus)int.MaxValue,
                TriggerQueueAdmissionReason.StorageUnavailable,
                envelope.DeliveryId,
                envelope.DeduplicationId,
                hash,
                null,
                null,
                null));
        }
    }

    private sealed class ContradictoryQueue : ITriggerQueueAdmissionPort
    {
        public int Calls { get; private set; }

        public Task<TriggerQueueAdmissionResult> AdmitAsync(
            TriggerQueueAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var envelope = request.DeliveryRequest.Envelope;
            Assert.True(TriggerDeliveryHash.TryCompute(envelope, out var hash, out _));
            return Task.FromResult(new TriggerQueueAdmissionResult(
                TriggerQueueAdmissionStatus.Rejected,
                TriggerQueueAdmissionReason.AdmissionRejected,
                envelope.DeliveryId,
                envelope.DeduplicationId,
                hash,
                null,
                TriggerAdmissionStatus.Admitted,
                TriggerAdmissionReason.EvidenceAccepted));
        }
    }

    private sealed class MissingAdmissionQueue : ITriggerQueueAdmissionPort
    {
        public int Calls { get; private set; }

        public Task<TriggerQueueAdmissionResult> AdmitAsync(
            TriggerQueueAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var envelope = request.DeliveryRequest.Envelope;
            Assert.True(TriggerDeliveryHash.TryCompute(envelope, out var hash, out _));
            return Task.FromResult(new TriggerQueueAdmissionResult(
                TriggerQueueAdmissionStatus.Queued,
                TriggerQueueAdmissionReason.Enqueued,
                envelope.DeliveryId,
                envelope.DeduplicationId,
                hash,
                null,
                null,
                null));
        }
    }
}
