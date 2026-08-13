using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Tests.Triggers.Schedules;

public sealed class ScheduleDueOccurrenceEvaluatorTests
{
    [Fact]
    public async Task Due_occurrence_is_claimed_prepared_queued_and_finalized_through_durable_phases()
    {
        var fixture = Fixture();

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        Assert.Equal(4, fixture.Store.Mutations.Count);
        Assert.Equal(2, fixture.CurrentEvidence.Calls);
        Assert.Equal(1, fixture.Overlap.Calls);
        Assert.Equal(1, fixture.Queue.Calls);
        Assert.Null(result.State!.PendingDelivery);
        Assert.Equal(2, result.State.NextOccurrence!.Ordinal);
        var terminal = Assert.Single(result.State.TerminalDeliveryEvidence);
        var prepared = fixture.Store.Mutations[1].Replacement.PendingDelivery!;
        Assert.Equal(ScheduleDeliveryResultKind.Queued, terminal.Result.Kind);
        Assert.Equal(prepared.CurrentEvidenceHash, terminal.CurrentEvidenceHash);
        Assert.Equal(prepared.RecurrenceProofHash, terminal.RecurrenceProofHash);
        Assert.Equal(new string('a', 64), terminal.OverlapEvidenceHash);
        Assert.True(
            ScheduleContractValidator.ValidateDefinitionStateComposition(fixture.Definition, result.State).IsValid,
            ScheduleEvaluatorTestData.Errors(
                ScheduleContractValidator.ValidateDefinitionStateComposition(fixture.Definition, result.State)));
        AssertLegalTransitions(fixture.Definition, fixture.Store.Mutations);
    }

    [Fact]
    public async Task Not_due_observation_is_persisted_and_later_clock_rollback_fails_closed()
    {
        var fixture = Fixture(now: ScheduleEvaluatorTestData.FirstUtc.AddMinutes(-5));
        fixture.Store.State = ScheduleEvaluatorTestData.State(
            fixture.Definition,
            lastClock: ScheduleEvaluatorTestData.FirstUtc.AddMinutes(-10));

        var notDue = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.NotDue, notDue.Status);
        Assert.Equal(ScheduleEvaluatorTestData.FirstUtc.AddMinutes(-5), notDue.State!.LastClockObservedAtUtc);
        Assert.Single(fixture.Store.Mutations);

        fixture.TimeProvider.Now = ScheduleEvaluatorTestData.FirstUtc.AddMinutes(-6);
        var rollback = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.ClockRollback, rollback.Status);
        Assert.Single(fixture.Store.Mutations);
    }

    [Fact]
    public async Task Occurrence_outside_trigger_horizon_is_deterministically_skipped_without_queue_attempt()
    {
        var fixture = Fixture(now: ScheduleEvaluatorTestData.FirstUtc.AddDays(31));

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Skipped, result.Status);
        Assert.Equal(2, result.State!.NextOccurrence!.Ordinal);
        var evidence = Assert.Single(result.State.DispositionEvidence);
        Assert.Equal(ScheduleOccurrenceDisposition.MisfireSkipped, evidence.Disposition);
        Assert.Equal("temporal-horizon-exceeded", evidence.ReasonCode);
        Assert.Equal(0, fixture.CurrentEvidence.Calls);
        Assert.Equal(0, fixture.Overlap.Calls);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Theory]
    [InlineData(ScheduleMisfirePolicyKind.Skip)]
    [InlineData(ScheduleMisfirePolicyKind.FireLatestOnce)]
    public async Task Non_catch_up_misfire_advances_exactly_one_occurrence_per_call(
        ScheduleMisfirePolicyKind policy)
    {
        var definition = ScheduleEvaluatorTestData.Definition(misfire: policy, catchUpLimit: 0);
        var fixture = Fixture(definition, now: ScheduleEvaluatorTestData.FirstUtc.AddDays(1).AddHours(1));

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Skipped, result.Status);
        Assert.Equal(2, result.State!.NextOccurrence!.Ordinal);
        Assert.Single(result.State.DispositionEvidence);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Skip_terminally_disposes_every_late_occurrence_while_fire_latest_delivers_the_latest_once()
    {
        var now = ScheduleEvaluatorTestData.FirstUtc.AddDays(1).AddHours(1);
        var skipDefinition = ScheduleEvaluatorTestData.Definition(
            misfire: ScheduleMisfirePolicyKind.Skip,
            catchUpLimit: 0);
        var skip = Fixture(skipDefinition, now: now);

        var firstSkip = await skip.Evaluator.EvaluateAsync(skipDefinition.ScheduleId);
        var secondSkip = await skip.Evaluator.EvaluateAsync(skipDefinition.ScheduleId);
        var skipSettled = await skip.Evaluator.EvaluateAsync(skipDefinition.ScheduleId);

        var latestDefinition = ScheduleEvaluatorTestData.Definition(
            misfire: ScheduleMisfirePolicyKind.FireLatestOnce,
            catchUpLimit: 0);
        var latest = Fixture(latestDefinition, now: now);

        var olderSkipped = await latest.Evaluator.EvaluateAsync(latestDefinition.ScheduleId);
        var latestDelivered = await latest.Evaluator.EvaluateAsync(latestDefinition.ScheduleId);
        var latestSettled = await latest.Evaluator.EvaluateAsync(latestDefinition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Skipped, firstSkip.Status);
        Assert.Equal(ScheduleEvaluationStatus.Skipped, secondSkip.Status);
        Assert.Equal(ScheduleEvaluationStatus.NotDue, skipSettled.Status);
        Assert.Equal(3, skipSettled.State!.NextOccurrence!.Ordinal);
        Assert.Equal(2, skipSettled.State.DispositionEvidence.Count);
        Assert.Equal(0, skip.Queue.Calls);
        Assert.Equal(ScheduleEvaluationStatus.Skipped, olderSkipped.Status);
        Assert.Equal(ScheduleEvaluationStatus.Queued, latestDelivered.Status);
        Assert.Equal(ScheduleEvaluationStatus.NotDue, latestSettled.Status);
        Assert.Equal(3, latestSettled.State!.NextOccurrence!.Ordinal);
        Assert.Single(latestSettled.State.DispositionEvidence);
        Assert.Single(latestSettled.State.TerminalDeliveryEvidence);
        Assert.Equal(1, latest.Queue.Calls);
    }

    [Fact]
    public async Task Catch_up_episode_is_frozen_and_budget_exhaustion_skips_remaining_due_occurrences()
    {
        var definition = ScheduleEvaluatorTestData.Definition(catchUpLimit: 2);
        var now = ScheduleEvaluatorTestData.FirstUtc.AddDays(2).AddHours(1);
        var fixture = Fixture(definition, now: now);

        var first = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, first.Status);
        Assert.Equal(2, first.State!.NextOccurrence!.Ordinal);
        Assert.Equal(new ScheduleCatchUpEpisode(1, 3, 1), first.State.CatchUpEpisode);

        var second = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, second.Status);
        Assert.Equal(4, second.State!.NextOccurrence!.Ordinal);
        Assert.Null(second.State.CatchUpEpisode);
        var skipped = Assert.Single(second.State.DispositionEvidence);
        Assert.Equal(3, skipped.FirstOrdinal);
        Assert.Equal(ScheduleOccurrenceDisposition.MisfireSkipped, skipped.Disposition);
        Assert.Equal(2, second.State.TerminalDeliveryEvidence.Count);
    }

    [Fact]
    public async Task Overlap_defer_retains_exact_identity_without_current_evidence_or_queue()
    {
        var fixture = Fixture();
        fixture.Overlap.Status = ScheduleOverlapStatus.Active;

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Deferred, result.Status);
        Assert.Null(result.State!.PendingDelivery);
        Assert.Equal(1, result.State.NextOccurrence!.Ordinal);
        Assert.NotNull(result.State.DeferredOccurrence);
        Assert.Equal(result.State.NextOccurrence, result.State.DeferredOccurrence!.Occurrence);
        var disposition = Assert.Single(result.State.DispositionEvidence);
        Assert.Equal(ScheduleOccurrenceDisposition.OverlapDeferred, disposition.Disposition);
        Assert.Equal(new string('a', 64), disposition.DecisionEvidenceHash);
        Assert.Equal(0, fixture.CurrentEvidence.Calls);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Deferred_occurrence_can_later_be_finally_skipped_without_rewriting_deferral_evidence()
    {
        var fixture = Fixture();
        fixture.Overlap.Status = ScheduleOverlapStatus.Active;
        var deferred = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);
        var retainedDeferral = Assert.Single(deferred.State!.DispositionEvidence);
        Assert.Equal(ScheduleOccurrenceDisposition.OverlapDeferred, retainedDeferral.Disposition);

        fixture.Overlap.Status = ScheduleOverlapStatus.Clear;
        fixture.TimeProvider.Now = ScheduleEvaluatorTestData.FirstUtc.AddDays(31);
        var skipped = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Skipped, skipped.Status);
        Assert.Null(skipped.State!.DeferredOccurrence);
        Assert.Contains(retainedDeferral, skipped.State.DispositionEvidence);
        Assert.Contains(
            skipped.State.DispositionEvidence,
            evidence => evidence.Disposition == ScheduleOccurrenceDisposition.MisfireSkipped
                && evidence.FirstOrdinal == retainedDeferral.FirstOrdinal);
        Assert.Equal(2, skipped.State.DispositionEvidence.Count);
        Assert.Equal(0, fixture.Queue.Calls);
        AssertLegalTransitions(fixture.Definition, fixture.Store.Mutations);
    }

    [Fact]
    public async Task Overlap_skip_retains_the_exact_decision_evidence_hash()
    {
        var definition = ScheduleEvaluatorTestData.Definition(overlap: ScheduleOverlapPolicy.Skip);
        var fixture = Fixture(definition);
        fixture.Overlap.Status = ScheduleOverlapStatus.Active;

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Skipped, result.Status);
        var disposition = Assert.Single(result.State!.DispositionEvidence);
        Assert.Equal(ScheduleOccurrenceDisposition.OverlapSkipped, disposition.Disposition);
        Assert.Equal(new string('a', 64), disposition.DecisionEvidenceHash);
        Assert.Equal(0, fixture.CurrentEvidence.Calls);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Initial_catch_up_overlap_skip_advances_without_forging_an_episode()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            catchUpLimit: 2,
            overlap: ScheduleOverlapPolicy.Skip);
        var fixture = Fixture(
            definition,
            now: ScheduleEvaluatorTestData.FirstUtc.AddDays(2).AddHours(1));
        fixture.Overlap.Status = ScheduleOverlapStatus.Active;

        var skipped = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Skipped, skipped.Status);
        Assert.Equal(2, skipped.State!.NextOccurrence!.Ordinal);
        Assert.Null(skipped.State.CatchUpEpisode);
        Assert.Equal(ScheduleOccurrenceDisposition.OverlapSkipped, Assert.Single(skipped.State.DispositionEvidence).Disposition);

        fixture.Overlap.Status = ScheduleOverlapStatus.Clear;
        var resumed = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, resumed.Status);
        Assert.Equal(3, resumed.State!.NextOccurrence!.Ordinal);
        Assert.Equal(new ScheduleCatchUpEpisode(1, 3, 1), resumed.State.CatchUpEpisode);
        AssertLegalTransitions(definition, fixture.Store.Mutations);
    }

    [Fact]
    public async Task Active_overlap_allow_continues_with_the_exact_overlap_evidence()
    {
        var definition = ScheduleEvaluatorTestData.Definition(overlap: ScheduleOverlapPolicy.Allow);
        var fixture = Fixture(definition);
        fixture.Overlap.Status = ScheduleOverlapStatus.Active;

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        Assert.Equal(2, fixture.CurrentEvidence.Calls);
        Assert.Equal(1, fixture.Queue.Calls);
        Assert.Equal(new string('a', ScheduleContractLimits.Sha256HexCharacters), Assert.Single(result.State!.TerminalDeliveryEvidence).OverlapEvidenceHash);
        AssertLegalTransitions(definition, fixture.Store.Mutations);
    }

    [Fact]
    public async Task Invalid_local_skip_is_durable_and_successor_advances_past_the_gap()
    {
        var firstLocal = new DateTime(2026, 3, 7, 2, 30, 0, DateTimeKind.Unspecified);
        var firstUtc = new DateTimeOffset(2026, 3, 7, 8, 30, 0, TimeSpan.Zero);
        var definition = ScheduleEvaluatorTestData.Definition(
            firstLocal: firstLocal,
            invalidLocal: ScheduleInvalidLocalTimePolicy.Skip);
        var fixture = Fixture(
            definition,
            ScheduleEvaluatorTestData.State(definition, ScheduleEvaluatorTestData.Occurrence(local: firstLocal, utc: firstUtc, timeZone: definition.TimeZone)),
            firstUtc.AddHours(1));
        fixture.TimeZone.LocalResolver = (timeZone, local) => local.Day == 8
            ? new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.InvalidLocalTime,
                timeZone.RulesFingerprint,
                local.AddMinutes(30),
                new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero),
                null)
            : new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                timeZone.RulesFingerprint,
                local,
                new DateTimeOffset(local.AddHours(6), TimeSpan.Zero),
                null);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        Assert.Equal(3, result.State!.NextOccurrence!.Ordinal);
        var skipped = Assert.Single(result.State.DispositionEvidence);
        Assert.Equal(2, skipped.FirstOrdinal);
        Assert.Equal(ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped, skipped.Disposition);
    }

    [Fact]
    public async Task Shift_forward_keeps_nominal_anchor_and_binds_first_valid_utc_into_proof()
    {
        var firstLocal = new DateTime(2026, 3, 7, 2, 30, 0, DateTimeKind.Unspecified);
        var firstUtc = new DateTimeOffset(2026, 3, 7, 8, 30, 0, TimeSpan.Zero);
        var definition = ScheduleEvaluatorTestData.Definition(firstLocal: firstLocal);
        var fixture = Fixture(
            definition,
            ScheduleEvaluatorTestData.State(definition, ScheduleEvaluatorTestData.Occurrence(local: firstLocal, utc: firstUtc, timeZone: definition.TimeZone)),
            firstUtc.AddHours(1));
        fixture.TimeZone.LocalResolver = (timeZone, local) => local == firstLocal
            ? new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                timeZone.RulesFingerprint,
                local,
                firstUtc,
                null)
            : new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.InvalidLocalTime,
                timeZone.RulesFingerprint,
                local.AddMinutes(30),
                new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero),
                null);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        Assert.Equal(new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified), result.State!.NextOccurrence!.ScheduledLocal);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero), result.State.NextOccurrence.ScheduledAtUtc);
        var preparedMutation = fixture.Store.Mutations[1].Replacement.PendingDelivery!;
        Assert.Matches("^[0-9a-f]{64}$", preparedMutation.RecurrenceProofHash!);
    }

    [Fact]
    public async Task Fixed_interval_uses_utc_to_local_port_and_preserves_folded_local_time()
    {
        var firstLocal = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified);
        var firstUtc = new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);
        var definition = ScheduleEvaluatorTestData.Definition(
            ScheduleRecurrenceKind.FixedInterval,
            firstLocal,
            3600);
        var state = ScheduleEvaluatorTestData.State(
            definition,
            ScheduleEvaluatorTestData.Occurrence(local: firstLocal, utc: firstUtc, timeZone: definition.TimeZone));
        var fixture = Fixture(definition, state, firstUtc.AddMinutes(10));
        fixture.TimeZone.InstantResolver = (timeZone, instant) => new ScheduleInstantResolution(
            ScheduleInstantResolutionStatus.Resolved,
            timeZone.RulesFingerprint,
            new DateTime(2026, 11, 1, 1, 0, 0, DateTimeKind.Unspecified));

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        Assert.Equal(firstUtc.AddHours(1), result.State!.NextOccurrence!.ScheduledAtUtc);
        Assert.Equal(new DateTime(2026, 11, 1, 1, 0, 0, DateTimeKind.Unspecified), result.State.NextOccurrence.ScheduledLocal);
        Assert.Equal(1, fixture.TimeZone.InstantCalls);
        Assert.Equal(1, fixture.TimeZone.LocalCalls);
    }

    [Fact]
    public async Task Fixed_interval_catch_up_compresses_large_budget_exhaustion_ranges_without_scanning()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            ScheduleRecurrenceKind.FixedInterval,
            intervalSeconds: 1,
            catchUpLimit: 2);
        var now = ScheduleEvaluatorTestData.FirstUtc.AddDays(20);
        var fixture = Fixture(definition, now: now);

        var first = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);
        var second = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, first.Status);
        Assert.Equal(new ScheduleCatchUpEpisode(1, 1_728_001, 1), first.State!.CatchUpEpisode);
        Assert.Equal(ScheduleEvaluationStatus.Queued, second.Status);
        Assert.Null(second.State!.CatchUpEpisode);
        Assert.Equal(1_728_002, second.State.NextOccurrence!.Ordinal);
        Assert.Equal(now.AddSeconds(1), second.State.NextOccurrence.ScheduledAtUtc);
        var skipped = Assert.Single(second.State.DispositionEvidence);
        Assert.Equal(3, skipped.FirstOrdinal);
        Assert.Equal(1_728_001, skipped.LastOrdinal);
        Assert.Equal(1_727_999, skipped.Count);
        Assert.Equal("catch-up-budget-exhausted", skipped.ReasonCode);
        Assert.Equal(5, fixture.TimeZone.InstantCalls);
    }

    [Theory]
    [InlineData(ScheduleRecurrenceKind.Once, 1L)]
    [InlineData(ScheduleRecurrenceKind.FixedInterval, ScheduleContractLimits.MaxOccurrenceOrdinal)]
    public async Task Exhausting_occurrence_still_revalidates_pinned_time_zone_rules(
        ScheduleRecurrenceKind recurrence,
        long ordinal)
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            recurrence,
            intervalSeconds: recurrence == ScheduleRecurrenceKind.FixedInterval ? 60 : null);
        var occurrence = ScheduleEvaluatorTestData.Occurrence(
            ordinal,
            definition.Recurrence.FirstLocalOccurrence,
            ScheduleEvaluatorTestData.FirstUtc,
            definition.TimeZone);
        var fixture = Fixture(
            definition,
            ScheduleEvaluatorTestData.State(definition, occurrence),
            ScheduleEvaluatorTestData.Now);
        fixture.TimeZone.LocalResolver = (timeZone, local) => new ScheduleTimeZoneResolution(
            ScheduleTimeZoneResolutionStatus.Unique,
            new string('d', ScheduleContractLimits.Sha256HexCharacters),
            local,
            occurrence.ScheduledAtUtc,
            null);
        fixture.TimeZone.InstantResolver = (timeZone, instant) => new ScheduleInstantResolution(
            ScheduleInstantResolutionStatus.Resolved,
            new string('d', ScheduleContractLimits.Sha256HexCharacters),
            occurrence.ScheduledLocal);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, result.Status);
        Assert.Equal("time-zone-rules-mismatch", result.ReasonCode);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Prepared_restart_uses_narrow_real_admission_refresh_without_changing_identity()
    {
        var initial = Fixture();
        _ = await initial.Evaluator.EvaluateAsync(initial.Definition.ScheduleId);
        var preparedState = initial.Store.Mutations
            .Select(mutation => mutation.Replacement)
            .Single(state => state.PendingDelivery?.Phase == SchedulePendingDeliveryPhase.Prepared);
        var prepared = preparedState.PendingDelivery!;
        var restartedAt = ScheduleEvaluatorTestData.Now
            + TriggerDeliveryLimits.MaxAdmissionAge
            + TimeSpan.FromTicks(1);
        var restarted = RealQueueFixture(initial.Definition, preparedState, restartedAt);
        restarted.CurrentEvidence.EvidenceHash = new string('b', 64);

        var result = await restarted.Evaluator.EvaluateAsync(initial.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        var commit = Assert.Single(restarted.QueueMutation.Requests);
        Assert.Equal(TriggerAdmissionStatus.Admitted, commit.AdmissionStatus);
        Assert.Equal(TriggerAdmissionReason.EvidenceAccepted, commit.AdmissionReason);
        Assert.Equal(prepared.Identity.DeliveryId, commit.Envelope.DeliveryId);
        Assert.Equal(prepared.Identity.DeduplicationId, commit.Envelope.DeduplicationId);
        Assert.Equal(prepared.Prepared!.CanonicalEnvelopeHash, commit.CanonicalEnvelopeHash);
        var terminal = Assert.Single(result.State!.TerminalDeliveryEvidence);
        Assert.Equal(new string('b', 64), terminal.CurrentEvidenceHash);
        Assert.NotEqual(prepared.CurrentEvidenceHash, terminal.CurrentEvidenceHash);
        Assert.Equal(prepared.Prepared.CanonicalEnvelopeHash, terminal.Result.CanonicalEnvelopeHash);
        Assert.Equal(restartedAt, restarted.Store.Mutations[0].Replacement.LastClockObservedAtUtc);
    }

    [Fact]
    public async Task Prepared_restart_reconciles_ambiguous_prior_success_as_exact_real_admission_replay()
    {
        var initial = Fixture();
        _ = await initial.Evaluator.EvaluateAsync(initial.Definition.ScheduleId);
        var preparedState = initial.Store.Mutations
            .Select(mutation => mutation.Replacement)
            .Single(state => state.PendingDelivery?.Phase == SchedulePendingDeliveryPhase.Prepared);
        var prepared = preparedState.PendingDelivery!;
        Assert.True(TriggerDeliveryAdmissionReceiptFactory.TryCreate(
            prepared.Prepared!.Envelope,
            TriggerAdmissionStatus.Admitted,
            TriggerAdmissionReason.EvidenceAccepted,
            prepared.Prepared.PreparedAtUtc,
            out var receipt,
            out var receiptValidation),
            string.Join(',', receiptValidation.Errors.Select(error => error.Code)));
        var history = new TriggerDeliveryAdmissionHistoryEntry(prepared.Prepared.Envelope, receipt!);
        var restarted = RealQueueFixture(
            initial.Definition,
            preparedState,
            ScheduleEvaluatorTestData.Now
                + TriggerDeliveryLimits.MaxAdmissionAge
                + TimeSpan.FromTicks(1),
            history);
        restarted.CurrentEvidence.EvidenceHash = new string('c', 64);

        var result = await restarted.Evaluator.EvaluateAsync(initial.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Replayed, result.Status);
        var commit = Assert.Single(restarted.QueueMutation.Requests);
        Assert.Equal(TriggerAdmissionStatus.Replayed, commit.AdmissionStatus);
        Assert.Equal(TriggerAdmissionReason.ExactReplay, commit.AdmissionReason);
        Assert.Equal(prepared.Identity.DeliveryId, commit.Envelope.DeliveryId);
        Assert.Equal(prepared.Identity.DeduplicationId, commit.Envelope.DeduplicationId);
        var terminal = Assert.Single(result.State!.TerminalDeliveryEvidence);
        Assert.Equal(ScheduleDeliveryResultKind.Replayed, terminal.Result.Kind);
        Assert.Equal(new string('c', 64), terminal.CurrentEvidenceHash);
        Assert.Equal(prepared.Prepared.CanonicalEnvelopeHash, terminal.Result.CanonicalEnvelopeHash);
        AssertLegalTransitions(initial.Definition, restarted.Store.Mutations);
    }

    [Fact]
    public async Task Later_evidence_resolution_uses_its_truthful_instant_for_real_admission_and_persistence()
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        var fixture = RealQueueFixture(
            definition,
            ScheduleEvaluatorTestData.State(definition),
            ScheduleEvaluatorTestData.Now);
        fixture.CurrentEvidence.ObservationDelay = TimeSpan.FromTicks(2);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.True(result.Status == ScheduleEvaluationStatus.Queued, result.ReasonCode);
        var preparationObservedAtUtc = ScheduleEvaluatorTestData.Now.AddTicks(2);
        var admissionObservedAtUtc = ScheduleEvaluatorTestData.Now.AddTicks(4);
        var commit = Assert.Single(fixture.QueueMutation.Requests);
        Assert.Equal(admissionObservedAtUtc, commit.RecordedAtUtc);
        Assert.Equal(admissionObservedAtUtc, result.State!.LastClockObservedAtUtc);
        Assert.Equal(preparationObservedAtUtc, fixture.Store.Mutations[1].Replacement.LastClockObservedAtUtc);
        Assert.Equal(admissionObservedAtUtc, fixture.Store.Mutations[2].Replacement.LastClockObservedAtUtc);
        Assert.Equal(
            admissionObservedAtUtc,
            commit.Receipt!.RecordedAtUtc);
    }

    [Fact]
    public async Task Authority_receipt_after_snapshot_observation_fails_closed_before_queue_mutation()
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        var fixture = RealQueueFixture(
            definition,
            ScheduleEvaluatorTestData.State(definition),
            ScheduleEvaluatorTestData.Now);
        fixture.CurrentEvidence.ObservationDelay = TimeSpan.FromTicks(2);
        fixture.CurrentEvidence.AuthorityLead = TimeSpan.FromTicks(1);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, result.Status);
        Assert.Equal("schedule-evidence-corrupt", result.ReasonCode);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Empty(fixture.QueueMutation.Requests);
    }

    [Fact]
    public async Task Evidence_observed_before_the_evaluator_clock_fails_closed()
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        var fixture = RealQueueFixture(
            definition,
            ScheduleEvaluatorTestData.State(definition),
            ScheduleEvaluatorTestData.Now);
        fixture.CurrentEvidence.ObservationDelay = TimeSpan.FromTicks(-1);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, result.Status);
        Assert.Empty(fixture.QueueMutation.Requests);
    }

    [Fact]
    public async Task Unavailable_pending_read_still_persists_clock_and_rejects_later_rollback()
    {
        var initial = Fixture();
        _ = await initial.Evaluator.EvaluateAsync(initial.Definition.ScheduleId);
        var preparedState = initial.Store.Mutations
            .Select(mutation => mutation.Replacement)
            .Single(state => state.PendingDelivery?.Phase == SchedulePendingDeliveryPhase.Prepared);
        var observedAt = ScheduleEvaluatorTestData.Now.AddMinutes(5);
        var restarted = Fixture(initial.Definition, preparedState, observedAt);
        restarted.CurrentEvidence.Status = ScheduleCurrentEvidenceStatus.Unavailable;

        var unavailable = await restarted.Evaluator.EvaluateAsync(initial.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Unavailable, unavailable.Status);
        Assert.Equal(observedAt, unavailable.State!.LastClockObservedAtUtc);
        Assert.Single(restarted.Store.Mutations);
        Assert.Equal(1, restarted.CurrentEvidence.Calls);
        restarted.TimeProvider.Now = observedAt.AddSeconds(-1);
        restarted.CurrentEvidence.Status = ScheduleCurrentEvidenceStatus.Available;

        var rollback = await restarted.Evaluator.EvaluateAsync(initial.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.ClockRollback, rollback.Status);
        Assert.Equal(1, restarted.CurrentEvidence.Calls);
        Assert.Empty(restarted.Queue.Requests);
    }

    [Fact]
    public async Task Permission_denial_leaves_the_exact_durable_claim_for_recovery()
    {
        var fixture = Fixture();
        fixture.CurrentEvidence.Status = ScheduleCurrentEvidenceStatus.PermissionDenied;

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.PermissionDenied, result.Status);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Single(fixture.Store.Mutations);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Ambiguous_queue_exception_is_persisted_and_never_redispatched_on_recovery()
    {
        var fixture = Fixture();
        fixture.Queue.Throw = true;

        var first = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.NeedsReview, first.Status);
        Assert.Equal(ScheduleDeliveryResultKind.Ambiguous, first.State!.PendingDelivery!.Result!.Kind);
        Assert.Equal(1, fixture.Queue.Calls);

        fixture.Queue.Throw = false;
        var recovered = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.NeedsReview, recovered.Status);
        Assert.Equal(1, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Queue_backpressure_retries_the_same_prepared_delivery_and_finalizes_once()
    {
        var fixture = Fixture();
        fixture.Queue.Status = TriggerQueueAdmissionStatus.Backpressured;

        var backpressured = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Backpressured, backpressured.Status);
        Assert.Equal(SchedulePendingDeliveryPhase.ResultObserved, backpressured.State!.PendingDelivery!.Phase);
        Assert.Equal(ScheduleDeliveryResultKind.Backpressured, backpressured.State.PendingDelivery.Result!.Kind);
        var deliveryId = backpressured.State.PendingDelivery.Identity.DeliveryId;
        var preparedHash = backpressured.State.PendingDelivery.Prepared!.CanonicalEnvelopeHash;

        fixture.Queue.Status = TriggerQueueAdmissionStatus.Queued;
        var recovered = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, recovered.Status);
        Assert.Null(recovered.State!.PendingDelivery);
        var terminal = Assert.Single(recovered.State.TerminalDeliveryEvidence);
        Assert.Equal(deliveryId, terminal.Identity.DeliveryId);
        Assert.Equal(preparedHash, terminal.Result.CanonicalEnvelopeHash);
        Assert.Equal(2, fixture.Queue.Calls);
        AssertLegalTransitions(fixture.Definition, fixture.Store.Mutations);
    }

    [Fact]
    public async Task Ambiguous_prepared_restart_retains_the_exact_refreshed_evidence_hash()
    {
        var initial = Fixture();
        _ = await initial.Evaluator.EvaluateAsync(initial.Definition.ScheduleId);
        var preparedState = initial.Store.Mutations
            .Select(mutation => mutation.Replacement)
            .Single(state => state.PendingDelivery?.Phase == SchedulePendingDeliveryPhase.Prepared);
        var restarted = Fixture(
            initial.Definition,
            preparedState,
            ScheduleEvaluatorTestData.Now.AddMinutes(5));
        restarted.CurrentEvidence.EvidenceHash = new string('d', 64);
        restarted.Queue.Throw = true;

        var result = await restarted.Evaluator.EvaluateAsync(initial.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.NeedsReview, result.Status);
        Assert.Equal(ScheduleDeliveryResultKind.Ambiguous, result.State!.PendingDelivery!.Result!.Kind);
        Assert.Equal(new string('d', 64), result.State.PendingDelivery.CurrentEvidenceHash);
        Assert.NotEqual(preparedState.PendingDelivery!.CurrentEvidenceHash, result.State.PendingDelivery.CurrentEvidenceHash);
    }

    [Fact]
    public async Task Optimistic_conflict_stops_before_external_evidence()
    {
        var fixture = Fixture();
        fixture.Store.NextMutationStatus = ScheduleStoreMutationStatus.Conflict;

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Conflict, result.Status);
        Assert.Equal(0, fixture.TimeZone.LocalCalls);
        Assert.Equal(0, fixture.Overlap.Calls);
        Assert.Equal(0, fixture.CurrentEvidence.Calls);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Corrupt_time_zone_fingerprint_fails_closed_after_claim()
    {
        var fixture = Fixture();
        fixture.TimeZone.LocalResolver = (_, local) => new ScheduleTimeZoneResolution(
            ScheduleTimeZoneResolutionStatus.Unique,
            new string('9', 64),
            local,
            new DateTimeOffset(local.AddHours(5), TimeSpan.Zero),
            null);

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, result.Status);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Disabled_idle_schedule_does_not_claim_but_disabled_state_with_pending_work_recovers()
    {
        var disabledDefinition = ScheduleEvaluatorTestData.Definition(enabled: false);
        var disabled = Fixture(disabledDefinition);
        var idle = await disabled.Evaluator.EvaluateAsync(disabledDefinition.ScheduleId);
        Assert.Equal(ScheduleEvaluationStatus.Disabled, idle.Status);
        Assert.Empty(disabled.Store.Mutations);

        var enabledDefinition = ScheduleEvaluatorTestData.Definition();
        var first = Fixture(enabledDefinition);
        first.CurrentEvidence.Status = ScheduleCurrentEvidenceStatus.PermissionDenied;
        var claimed = await first.Evaluator.EvaluateAsync(enabledDefinition.ScheduleId);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, claimed.State!.PendingDelivery!.Phase);
        first.Store.State = claimed.State with { Enabled = false };
        first.CurrentEvidence.Status = ScheduleCurrentEvidenceStatus.Available;

        var recovered = await first.Evaluator.EvaluateAsync(enabledDefinition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, recovered.Status);
    }

    [Theory]
    [InlineData(ScheduleStoreReadStatus.Unavailable, ScheduleEvaluationStatus.Unavailable, "schedule-store-unavailable")]
    [InlineData(ScheduleStoreReadStatus.Corrupt, ScheduleEvaluationStatus.Corrupt, "schedule-store-corrupt")]
    [InlineData(ScheduleStoreReadStatus.Backpressured, ScheduleEvaluationStatus.Backpressured, "schedule-store-backpressured")]
    public async Task Store_read_failures_preserve_the_closed_status_and_reason(
        ScheduleStoreReadStatus storeStatus,
        ScheduleEvaluationStatus expectedStatus,
        string expectedReason)
    {
        var fixture = Fixture();
        fixture.Store.ReadStatus = storeStatus;

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Empty(fixture.Store.Mutations);
    }

    [Theory]
    [InlineData(ScheduleStoreMutationStatus.Unavailable, ScheduleEvaluationStatus.Unavailable, "schedule-store-unavailable")]
    [InlineData(ScheduleStoreMutationStatus.Corrupt, ScheduleEvaluationStatus.Corrupt, "schedule-store-corrupt")]
    [InlineData(ScheduleStoreMutationStatus.Backpressured, ScheduleEvaluationStatus.Backpressured, "schedule-store-backpressured")]
    public async Task Mutation_failures_preserve_the_closed_status_and_reason(
        ScheduleStoreMutationStatus storeStatus,
        ScheduleEvaluationStatus expectedStatus,
        string expectedReason)
    {
        var fixture = Fixture();
        fixture.Store.NextMutationStatus = storeStatus;

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Single(fixture.Store.Mutations);
        Assert.Equal(0, fixture.CurrentEvidence.Calls);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Theory]
    [InlineData(ScheduleCurrentEvidenceStatus.PermissionDenied, ScheduleEvaluationStatus.PermissionDenied, "schedule-permission-denied")]
    [InlineData(ScheduleCurrentEvidenceStatus.RecurrenceDenied, ScheduleEvaluationStatus.PermissionDenied, "schedule-recurrence-denied")]
    [InlineData(ScheduleCurrentEvidenceStatus.TargetUnavailable, ScheduleEvaluationStatus.Unavailable, "schedule-target-unavailable")]
    [InlineData(ScheduleCurrentEvidenceStatus.AdapterUnavailable, ScheduleEvaluationStatus.Unavailable, "schedule-adapter-unavailable")]
    [InlineData(ScheduleCurrentEvidenceStatus.ActorUnavailable, ScheduleEvaluationStatus.Unavailable, "schedule-actor-unavailable")]
    [InlineData(ScheduleCurrentEvidenceStatus.AuthorityUnavailable, ScheduleEvaluationStatus.Unavailable, "schedule-authority-unavailable")]
    [InlineData(ScheduleCurrentEvidenceStatus.PayloadUnavailable, ScheduleEvaluationStatus.Unavailable, "schedule-payload-unavailable")]
    [InlineData(ScheduleCurrentEvidenceStatus.Unavailable, ScheduleEvaluationStatus.Unavailable, "schedule-evidence-unavailable")]
    [InlineData(ScheduleCurrentEvidenceStatus.Corrupt, ScheduleEvaluationStatus.Corrupt, "schedule-evidence-corrupt")]
    [InlineData(ScheduleCurrentEvidenceStatus.Backpressured, ScheduleEvaluationStatus.Backpressured, "schedule-evidence-backpressured")]
    public async Task Current_evidence_failures_preserve_the_closed_status_and_reason(
        ScheduleCurrentEvidenceStatus evidenceStatus,
        ScheduleEvaluationStatus expectedStatus,
        string expectedReason)
    {
        var fixture = Fixture();
        fixture.CurrentEvidence.Status = evidenceStatus;

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Theory]
    [InlineData(ScheduleOverlapStatus.Unavailable, ScheduleEvaluationStatus.Unavailable, "overlap-evidence-unavailable")]
    [InlineData(ScheduleOverlapStatus.Corrupt, ScheduleEvaluationStatus.Corrupt, "overlap-evidence-corrupt")]
    [InlineData(ScheduleOverlapStatus.Backpressured, ScheduleEvaluationStatus.Backpressured, "overlap-evidence-backpressured")]
    public async Task Overlap_failures_preserve_the_closed_status_and_reason(
        ScheduleOverlapStatus overlapStatus,
        ScheduleEvaluationStatus expectedStatus,
        string expectedReason)
    {
        var fixture = Fixture();
        fixture.Overlap.Status = overlapStatus;

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Equal(0, fixture.CurrentEvidence.Calls);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Theory]
    [InlineData(ScheduleTimeZoneResolutionStatus.Unavailable, ScheduleEvaluationStatus.Unavailable, "time-zone-unavailable")]
    [InlineData(ScheduleTimeZoneResolutionStatus.Corrupt, ScheduleEvaluationStatus.Corrupt, "time-zone-corrupt")]
    [InlineData(ScheduleTimeZoneResolutionStatus.Backpressured, ScheduleEvaluationStatus.Backpressured, "time-zone-backpressured")]
    public async Task Local_time_zone_failures_preserve_the_closed_status_and_reason(
        ScheduleTimeZoneResolutionStatus resolutionStatus,
        ScheduleEvaluationStatus expectedStatus,
        string expectedReason)
    {
        var fixture = Fixture();
        fixture.TimeZone.LocalResolver = (timeZone, local) => new ScheduleTimeZoneResolution(
            resolutionStatus,
            timeZone.RulesFingerprint,
            local,
            null,
            null);

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Theory]
    [InlineData(ScheduleInstantResolutionStatus.Unavailable, ScheduleEvaluationStatus.Unavailable, "time-zone-unavailable")]
    [InlineData(ScheduleInstantResolutionStatus.Corrupt, ScheduleEvaluationStatus.Corrupt, "time-zone-corrupt")]
    [InlineData(ScheduleInstantResolutionStatus.Backpressured, ScheduleEvaluationStatus.Backpressured, "time-zone-backpressured")]
    public async Task Fixed_interval_time_zone_failures_preserve_the_closed_status_and_reason(
        ScheduleInstantResolutionStatus resolutionStatus,
        ScheduleEvaluationStatus expectedStatus,
        string expectedReason)
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            intervalSeconds: 60);
        var fixture = Fixture(definition);
        fixture.TimeZone.InstantResolver = (timeZone, instant) => new ScheduleInstantResolution(
            resolutionStatus,
            timeZone.RulesFingerprint,
            DateTime.SpecifyKind(instant.UtcDateTime.AddHours(-5), DateTimeKind.Unspecified));

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Theory]
    [InlineData(TriggerQueueAdmissionStatus.Replayed, ScheduleEvaluationStatus.Replayed, "queue-exact-replay")]
    [InlineData(TriggerQueueAdmissionStatus.Rejected, ScheduleEvaluationStatus.Rejected, "queue-admission-rejected")]
    [InlineData(TriggerQueueAdmissionStatus.Unavailable, ScheduleEvaluationStatus.NeedsReview, "queue-outcome-ambiguous")]
    public async Task Closed_queue_outcomes_are_durably_projected(
        TriggerQueueAdmissionStatus queueStatus,
        ScheduleEvaluationStatus expectedStatus,
        string expectedReason)
    {
        var fixture = Fixture();
        fixture.Queue.Status = queueStatus;

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Equal(1, fixture.Queue.Calls);
        if (expectedStatus == ScheduleEvaluationStatus.NeedsReview)
        {
            Assert.Equal(SchedulePendingDeliveryPhase.ResultObserved, result.State!.PendingDelivery!.Phase);
        }
        else
        {
            Assert.Null(result.State!.PendingDelivery);
        }
    }

    [Fact]
    public async Task Missing_exhausted_and_mismatched_schedules_fail_before_claiming()
    {
        var missing = Fixture();
        missing.Store.ReadStatus = ScheduleStoreReadStatus.NotFound;
        var missingResult = await missing.Evaluator.EvaluateAsync(missing.Definition.ScheduleId);

        var once = ScheduleEvaluatorTestData.Definition(recurrence: ScheduleRecurrenceKind.Once);
        var exhaustedState = ScheduleEvaluatorTestData.State(once) with { NextOccurrence = null };
        var exhausted = Fixture(once, exhaustedState);
        var exhaustedResult = await exhausted.Evaluator.EvaluateAsync(once.ScheduleId);

        var definition = ScheduleEvaluatorTestData.Definition();
        var mismatchedState = ScheduleEvaluatorTestData.State(definition) with
        {
            DefinitionHash = new string('0', ScheduleContractLimits.Sha256HexCharacters),
        };
        var mismatched = Fixture(definition, mismatchedState);
        var mismatchedResult = await mismatched.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.NotFound, missingResult.Status);
        Assert.Equal("schedule-not-found", missingResult.ReasonCode);
        Assert.Equal(ScheduleEvaluationStatus.Exhausted, exhaustedResult.Status);
        Assert.Equal("schedule-exhausted", exhaustedResult.ReasonCode);
        Assert.Equal(ScheduleEvaluationStatus.Corrupt, mismatchedResult.Status);
        Assert.Equal("definition-state-invalid", mismatchedResult.ReasonCode);
        Assert.Empty(missing.Store.Mutations);
        Assert.Empty(exhausted.Store.Mutations);
        Assert.Empty(mismatched.Store.Mutations);
    }

    [Fact]
    public async Task Dependency_exceptions_fail_closed_at_each_pre_effect_boundary()
    {
        var read = Fixture();
        read.Store.ThrowOnRead = true;
        var readResult = await read.Evaluator.EvaluateAsync(read.Definition.ScheduleId);

        var mutation = Fixture();
        mutation.Store.ThrowOnMutation = true;
        var mutationResult = await mutation.Evaluator.EvaluateAsync(mutation.Definition.ScheduleId);

        var timeZone = Fixture();
        timeZone.TimeZone.LocalResolver = (_, _) => throw new IOException("time-zone unavailable");
        var timeZoneResult = await timeZone.Evaluator.EvaluateAsync(timeZone.Definition.ScheduleId);

        var overlap = Fixture();
        overlap.Overlap.Throw = true;
        var overlapResult = await overlap.Evaluator.EvaluateAsync(overlap.Definition.ScheduleId);

        var evidence = Fixture();
        evidence.CurrentEvidence.Throw = true;
        var evidenceResult = await evidence.Evaluator.EvaluateAsync(evidence.Definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.Unavailable, "schedule-store-unavailable"),
            (readResult.Status, readResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.Unavailable, "schedule-store-unavailable"),
            (mutationResult.Status, mutationResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.Unavailable, "time-zone-unavailable"),
            (timeZoneResult.Status, timeZoneResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.Unavailable, "overlap-evidence-unavailable"),
            (overlapResult.Status, overlapResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.Unavailable, "schedule-evidence-unavailable"),
            (evidenceResult.Status, evidenceResult.ReasonCode));
        Assert.Equal(0, read.Queue.Calls);
        Assert.Equal(0, mutation.Queue.Calls);
        Assert.Equal(0, timeZone.Queue.Calls);
        Assert.Equal(0, overlap.Queue.Calls);
        Assert.Equal(0, evidence.Queue.Calls);
    }

    [Fact]
    public async Task Revision_exhaustion_blocks_claim_and_not_due_observation()
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        var dueState = ScheduleEvaluatorTestData.State(definition) with
        {
            StateRevision = ScheduleContractLimits.MaxRevision,
        };
        var due = Fixture(definition, dueState);
        var dueResult = await due.Evaluator.EvaluateAsync(definition.ScheduleId);

        var notDueNow = ScheduleEvaluatorTestData.FirstUtc.AddMinutes(-5);
        var notDueState = ScheduleEvaluatorTestData.State(
            definition,
            revision: ScheduleContractLimits.MaxRevision,
            lastClock: notDueNow.AddMinutes(-1));
        var notDue = Fixture(definition, notDueState, notDueNow);
        var notDueResult = await notDue.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.BoundExceeded, dueResult.Status);
        Assert.Equal("claim-coordinates-invalid", dueResult.ReasonCode);
        Assert.Equal(ScheduleEvaluationStatus.BoundExceeded, notDueResult.Status);
        Assert.Equal("state-revision-exhausted", notDueResult.ReasonCode);
        Assert.Empty(due.Store.Mutations);
        Assert.Empty(notDue.Store.Mutations);
    }

    [Fact]
    public async Task Ambiguous_current_occurrence_uses_the_pinned_later_utc_policy()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            ambiguousLocal: ScheduleAmbiguousLocalTimePolicy.LaterUtc);
        var fixture = Fixture(definition);
        fixture.TimeZone.LocalResolver = (timeZone, local) => local == definition.Recurrence.FirstLocalOccurrence
            ? new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.AmbiguousLocalTime,
                timeZone.RulesFingerprint,
                local,
                ScheduleEvaluatorTestData.FirstUtc.AddHours(-1),
                ScheduleEvaluatorTestData.FirstUtc)
            : new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                timeZone.RulesFingerprint,
                local,
                new DateTimeOffset(local.AddHours(5), TimeSpan.Zero),
                null);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        Assert.Equal(ScheduleEvaluatorTestData.FirstUtc, result.State!.TerminalDeliveryEvidence[0].Occurrence.ScheduledAtUtc);
    }

    [Fact]
    public async Task Ambiguous_successor_uses_the_pinned_later_utc_policy()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            ambiguousLocal: ScheduleAmbiguousLocalTimePolicy.LaterUtc);
        var nextLocal = definition.Recurrence.FirstLocalOccurrence.AddDays(1);
        var earlier = ScheduleEvaluatorTestData.FirstUtc.AddDays(1);
        var fixture = Fixture(definition);
        fixture.TimeZone.LocalResolver = (timeZone, local) => local == nextLocal
            ? new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.AmbiguousLocalTime,
                timeZone.RulesFingerprint,
                local,
                earlier,
                earlier.AddHours(1))
            : new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                timeZone.RulesFingerprint,
                local,
                ScheduleEvaluatorTestData.FirstUtc,
                null);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        Assert.Equal(earlier.AddHours(1), result.State!.NextOccurrence!.ScheduledAtUtc);
    }

    [Theory]
    [InlineData(ScheduleTimeZoneResolutionStatus.Unavailable, ScheduleEvaluationStatus.Unavailable, "time-zone-unavailable")]
    [InlineData(ScheduleTimeZoneResolutionStatus.Corrupt, ScheduleEvaluationStatus.Corrupt, "time-zone-corrupt")]
    [InlineData(ScheduleTimeZoneResolutionStatus.Backpressured, ScheduleEvaluationStatus.Backpressured, "time-zone-backpressured")]
    public async Task Successor_local_time_zone_failures_preserve_the_closed_status_and_reason(
        ScheduleTimeZoneResolutionStatus resolutionStatus,
        ScheduleEvaluationStatus expectedStatus,
        string expectedReason)
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        var fixture = Fixture(definition);
        fixture.TimeZone.LocalResolver = (timeZone, local) => local == definition.Recurrence.FirstLocalOccurrence
            ? new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                timeZone.RulesFingerprint,
                local,
                ScheduleEvaluatorTestData.FirstUtc,
                null)
            : new ScheduleTimeZoneResolution(
                resolutionStatus,
                timeZone.RulesFingerprint,
                local,
                null,
                null);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Theory]
    [InlineData(ScheduleInstantResolutionStatus.Unavailable, ScheduleEvaluationStatus.Unavailable, "time-zone-unavailable")]
    [InlineData(ScheduleInstantResolutionStatus.Corrupt, ScheduleEvaluationStatus.Corrupt, "time-zone-corrupt")]
    [InlineData(ScheduleInstantResolutionStatus.Backpressured, ScheduleEvaluationStatus.Backpressured, "time-zone-backpressured")]
    public async Task Current_fixed_interval_time_zone_failures_preserve_the_closed_status_and_reason(
        ScheduleInstantResolutionStatus resolutionStatus,
        ScheduleEvaluationStatus expectedStatus,
        string expectedReason)
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            intervalSeconds: 60);
        var occurrence = ScheduleEvaluatorTestData.Occurrence(
            ordinal: 2,
            local: ScheduleEvaluatorTestData.FirstLocal.AddMinutes(1),
            utc: ScheduleEvaluatorTestData.FirstUtc.AddMinutes(1),
            timeZone: definition.TimeZone);
        var fixture = Fixture(definition, ScheduleEvaluatorTestData.State(definition, occurrence));
        fixture.TimeZone.InstantResolver = (timeZone, instant) => new ScheduleInstantResolution(
            resolutionStatus,
            timeZone.RulesFingerprint,
            occurrence.ScheduledLocal);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Theory]
    [InlineData(ScheduleRecurrenceKind.Once, 1L)]
    [InlineData(ScheduleRecurrenceKind.FixedInterval, ScheduleContractLimits.MaxOccurrenceOrdinal)]
    public async Task Valid_terminal_occurrence_queues_once_then_reports_exhausted(
        ScheduleRecurrenceKind recurrence,
        long ordinal)
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            recurrence: recurrence,
            intervalSeconds: recurrence == ScheduleRecurrenceKind.FixedInterval ? 60 : null);
        var occurrence = ScheduleEvaluatorTestData.Occurrence(
            ordinal: ordinal,
            timeZone: definition.TimeZone);
        var fixture = Fixture(definition, ScheduleEvaluatorTestData.State(definition, occurrence));

        var queued = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);
        var exhausted = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, queued.Status);
        Assert.Null(queued.State!.NextOccurrence);
        Assert.Equal(ScheduleEvaluationStatus.Exhausted, exhausted.Status);
        Assert.Equal("schedule-exhausted", exhausted.ReasonCode);
        Assert.Equal(1, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Successor_resolution_exceptions_fail_closed_without_queueing()
    {
        var daily = ScheduleEvaluatorTestData.Definition();
        var dailyFixture = Fixture(daily);
        dailyFixture.TimeZone.LocalResolver = (_, local) => local == daily.Recurrence.FirstLocalOccurrence
            ? new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                daily.TimeZone.RulesFingerprint,
                local,
                ScheduleEvaluatorTestData.FirstUtc,
                null)
            : throw new IOException("successor time-zone unavailable");
        var dailyResult = await dailyFixture.Evaluator.EvaluateAsync(daily.ScheduleId);

        var fixedDefinition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            intervalSeconds: 60);
        var fixedFixture = Fixture(fixedDefinition);
        fixedFixture.TimeZone.InstantResolver = (_, _) => throw new IOException("successor time-zone unavailable");
        var fixedResult = await fixedFixture.Evaluator.EvaluateAsync(fixedDefinition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.Unavailable, "time-zone-unavailable"),
            (dailyResult.Status, dailyResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.Unavailable, "time-zone-unavailable"),
            (fixedResult.Status, fixedResult.ReasonCode));
        Assert.Equal(0, dailyFixture.Queue.Calls);
        Assert.Equal(0, fixedFixture.Queue.Calls);
    }

    [Fact]
    public async Task Daily_catch_up_limit_one_queues_once_and_durably_skips_the_remaining_episode()
    {
        var definition = ScheduleEvaluatorTestData.Definition(catchUpLimit: 1);
        var now = ScheduleEvaluatorTestData.FirstUtc.AddDays(2).AddHours(1);
        var fixture = Fixture(definition, now: now);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        Assert.Null(result.State!.CatchUpEpisode);
        Assert.Equal(4, result.State.NextOccurrence!.Ordinal);
        Assert.Equal([2L, 3L], result.State.DispositionEvidence.Select(evidence => evidence.FirstOrdinal));
        Assert.All(result.State.DispositionEvidence, evidence =>
            Assert.Equal("catch-up-budget-exhausted", evidence.ReasonCode));
        Assert.Equal(1, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Daily_catch_up_budget_above_two_advances_the_frozen_episode_one_occurrence_at_a_time()
    {
        var definition = ScheduleEvaluatorTestData.Definition(catchUpLimit: 3);
        var now = ScheduleEvaluatorTestData.FirstUtc.AddDays(3).AddHours(1);
        var fixture = Fixture(definition, now: now);

        var first = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);
        var second = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);
        var third = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(new ScheduleCatchUpEpisode(1, 4, 2), first.State!.CatchUpEpisode);
        Assert.Equal(new ScheduleCatchUpEpisode(1, 4, 1), second.State!.CatchUpEpisode);
        Assert.Null(third.State!.CatchUpEpisode);
        Assert.Equal(5, third.State.NextOccurrence!.Ordinal);
        Assert.Equal(3, fixture.Queue.Calls);
        Assert.Equal(3, third.State.TerminalDeliveryEvidence.Count);
    }

    [Fact]
    public async Task Daily_catch_up_retains_invalid_local_skip_evidence_before_the_next_admitted_occurrence()
    {
        var firstLocal = new DateTime(2026, 3, 7, 2, 30, 0, DateTimeKind.Unspecified);
        var firstUtc = new DateTimeOffset(2026, 3, 7, 8, 30, 0, TimeSpan.Zero);
        var definition = ScheduleEvaluatorTestData.Definition(
            firstLocal: firstLocal,
            catchUpLimit: 2,
            invalidLocal: ScheduleInvalidLocalTimePolicy.Skip);
        var fixture = Fixture(
            definition,
            ScheduleEvaluatorTestData.State(
                definition,
                ScheduleEvaluatorTestData.Occurrence(local: firstLocal, utc: firstUtc, timeZone: definition.TimeZone)),
            firstUtc.AddDays(2).AddHours(1));
        fixture.TimeZone.LocalResolver = (timeZone, local) => local.Day == 8
            ? new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.InvalidLocalTime,
                timeZone.RulesFingerprint,
                local.AddMinutes(30),
                new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero),
                null)
            : new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                timeZone.RulesFingerprint,
                local,
                new DateTimeOffset(local.AddHours(6), TimeSpan.Zero),
                null);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        Assert.Equal(new ScheduleCatchUpEpisode(1, 3, 1), result.State!.CatchUpEpisode);
        var plan = fixture.Store.Mutations[1].Replacement.PendingDelivery!.FinalizationPlan!;
        var skip = Assert.Single(plan.DispositionEvidence);
        Assert.Equal(ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped, skip.Disposition);
        Assert.Equal(2, skip.FirstOrdinal);
    }

    [Fact]
    public async Task Active_catch_up_uses_its_frozen_episode_when_the_occurrence_crosses_the_temporal_horizon()
    {
        var definition = ScheduleEvaluatorTestData.Definition(catchUpLimit: 2);
        var fixture = Fixture(
            definition,
            now: ScheduleEvaluatorTestData.FirstUtc.AddDays(2).AddHours(1));
        var first = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);
        Assert.NotNull(first.State!.CatchUpEpisode);

        fixture.TimeProvider.Now = ScheduleEvaluatorTestData.FirstUtc.AddDays(35);
        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Skipped, result.Status);
        Assert.Null(result.State!.CatchUpEpisode);
        Assert.Equal(4, result.State.NextOccurrence!.Ordinal);
        Assert.Contains(result.State.DispositionEvidence,
            evidence => evidence.ReasonCode == "temporal-horizon-exceeded");
        Assert.Contains(result.State.DispositionEvidence,
            evidence => evidence.FirstOrdinal == 3
                && evidence.LastOrdinal == 3
                && evidence.ReasonCode == "catch-up-budget-exhausted");
        Assert.Equal(1, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Fixed_interval_catch_up_limit_one_compresses_the_skipped_range()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            intervalSeconds: 60,
            catchUpLimit: 1);
        var now = ScheduleEvaluatorTestData.FirstUtc.AddMinutes(5);
        var fixture = Fixture(definition, now: now);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        Assert.Null(result.State!.CatchUpEpisode);
        Assert.Equal(7, result.State.NextOccurrence!.Ordinal);
        var skipped = Assert.Single(result.State.DispositionEvidence);
        Assert.Equal(2, skipped.FirstOrdinal);
        Assert.Equal(6, skipped.LastOrdinal);
        Assert.Equal(5, skipped.Count);
    }

    [Fact]
    public async Task Fixed_interval_catch_up_budget_above_two_advances_the_frozen_episode()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            intervalSeconds: 60,
            catchUpLimit: 3);
        var fixture = Fixture(definition, now: ScheduleEvaluatorTestData.FirstUtc.AddMinutes(5));

        var first = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);
        var second = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);
        var third = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(new ScheduleCatchUpEpisode(1, 6, 2), first.State!.CatchUpEpisode);
        Assert.Equal(new ScheduleCatchUpEpisode(1, 6, 1), second.State!.CatchUpEpisode);
        Assert.Null(third.State!.CatchUpEpisode);
        Assert.Equal(7, third.State.NextOccurrence!.Ordinal);
        Assert.Equal(3, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Current_shift_forward_occurrence_revalidates_the_exact_nominal_anchor()
    {
        var nominal = new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified);
        var shiftedUtc = new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero);
        var definition = ScheduleEvaluatorTestData.Definition(firstLocal: nominal);
        var occurrence = ScheduleEvaluatorTestData.Occurrence(
            local: nominal,
            utc: shiftedUtc,
            timeZone: definition.TimeZone);
        var fixture = Fixture(definition, ScheduleEvaluatorTestData.State(definition, occurrence), shiftedUtc.AddHours(1));
        fixture.TimeZone.LocalResolver = (timeZone, local) => local == nominal
            ? new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.InvalidLocalTime,
                timeZone.RulesFingerprint,
                local.AddMinutes(30),
                shiftedUtc,
                null)
            : new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                timeZone.RulesFingerprint,
                local,
                new DateTimeOffset(local.AddHours(5), TimeSpan.Zero),
                null);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        Assert.Equal(nominal.AddDays(1), result.State!.NextOccurrence!.ScheduledLocal);
        Assert.Equal([nominal, nominal.AddDays(1)], fixture.TimeZone.LocalRequests);
    }

    [Fact]
    public async Task Existing_deferral_is_idempotently_retained_while_overlap_remains_active()
    {
        var fixture = Fixture();
        fixture.Overlap.Status = ScheduleOverlapStatus.Active;
        var first = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);
        var retained = Assert.Single(first.State!.DispositionEvidence);

        var second = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Deferred, second.Status);
        Assert.Equal(first.State.DeferredOccurrence, second.State!.DeferredOccurrence);
        Assert.Equal([retained], second.State.DispositionEvidence);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Pending_revision_exhaustion_blocks_clock_observation_and_preparation()
    {
        var seed = Fixture();
        seed.CurrentEvidence.Status = ScheduleCurrentEvidenceStatus.PermissionDenied;
        var claimed = await seed.Evaluator.EvaluateAsync(seed.Definition.ScheduleId);

        var observeState = claimed.State! with
        {
            StateRevision = ScheduleContractLimits.MaxRevision,
            LastClockObservedAtUtc = ScheduleEvaluatorTestData.Now,
        };
        var observe = Fixture(
            seed.Definition,
            observeState,
            ScheduleEvaluatorTestData.Now.AddSeconds(1));
        var observeResult = await observe.Evaluator.EvaluateAsync(seed.Definition.ScheduleId);

        var prepareState = claimed.State! with
        {
            StateRevision = ScheduleContractLimits.MaxRevision,
        };
        var prepare = Fixture(seed.Definition, prepareState, ScheduleEvaluatorTestData.Now);
        var prepareResult = await prepare.Evaluator.EvaluateAsync(seed.Definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.BoundExceeded, "state-revision-exhausted"),
            (observeResult.Status, observeResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.BoundExceeded, "state-revision-exhausted"),
            (prepareResult.Status, prepareResult.ReasonCode));
        Assert.Empty(observe.Store.Mutations);
        Assert.Empty(prepare.Store.Mutations);
        Assert.Equal(0, prepare.Queue.Calls);
    }

    [Fact]
    public async Task Not_due_with_an_exact_clock_watermark_is_read_only()
    {
        var now = ScheduleEvaluatorTestData.FirstUtc.AddMinutes(-5);
        var definition = ScheduleEvaluatorTestData.Definition();
        var state = ScheduleEvaluatorTestData.State(definition, lastClock: now);
        var fixture = Fixture(definition, state, now);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.NotDue, result.Status);
        Assert.Equal("occurrence-not-due", result.ReasonCode);
        Assert.Empty(fixture.Store.Mutations);
    }

    [Fact]
    public async Task Clock_failures_are_closed_before_claiming()
    {
        var thrown = Fixture();
        thrown.TimeProvider.Throw = true;
        var thrownResult = await thrown.Evaluator.EvaluateAsync(thrown.Definition.ScheduleId);

        var nonUtc = Fixture(now: new DateTimeOffset(2026, 8, 12, 9, 30, 0, TimeSpan.FromHours(-5)));
        var nonUtcResult = await nonUtc.Evaluator.EvaluateAsync(nonUtc.Definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.Unavailable, "schedule-clock-unavailable"),
            (thrownResult.Status, thrownResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.Corrupt, "schedule-clock-invalid"),
            (nonUtcResult.Status, nonUtcResult.ReasonCode));
        Assert.Empty(thrown.Store.Mutations);
        Assert.Empty(nonUtc.Store.Mutations);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_across_every_async_dependency_boundary()
    {
        await AssertCanceledAsync(
            fixture => fixture.Store.CancelOnRead = true,
            fixture => fixture.Store.LastReadCancellationToken);
        await AssertCanceledAsync(
            fixture => fixture.Store.CancelOnMutation = true,
            fixture => fixture.Store.LastMutationCancellationToken);
        await AssertCanceledAsync(
            fixture => fixture.TimeZone.CancelOnLocalCall = 1,
            fixture => fixture.TimeZone.LastLocalCancellationToken);
        await AssertCanceledAsync(
            fixture => fixture.TimeZone.CancelOnLocalCall = 2,
            fixture => fixture.TimeZone.LastLocalCancellationToken);
        await AssertCanceledAsync(
            fixture => fixture.Overlap.Cancel = true,
            fixture => fixture.Overlap.LastCancellationToken);
        await AssertCanceledAsync(
            fixture => fixture.CurrentEvidence.Cancel = true,
            fixture => fixture.CurrentEvidence.LastCancellationToken);
        await AssertCanceledAsync(
            fixture => fixture.Queue.Cancel = true,
            fixture => fixture.Queue.LastCancellationToken);

        var fixedDefinition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            intervalSeconds: 60);
        await AssertCanceledAsync(
            fixture => fixture.TimeZone.CancelOnInstantCall = 1,
            fixture => fixture.TimeZone.LastInstantCancellationToken,
            () => Fixture(fixedDefinition));

        var current = ScheduleEvaluatorTestData.Occurrence(
            ordinal: 2,
            local: ScheduleEvaluatorTestData.FirstLocal.AddMinutes(1),
            utc: ScheduleEvaluatorTestData.FirstUtc.AddMinutes(1),
            timeZone: fixedDefinition.TimeZone);
        await AssertCanceledAsync(
            fixture => fixture.TimeZone.CancelOnInstantCall = 1,
            fixture => fixture.TimeZone.LastInstantCancellationToken,
            () => Fixture(
                fixedDefinition,
                ScheduleEvaluatorTestData.State(fixedDefinition, current)));
    }

    [Theory]
    [InlineData(SchedulePriority.Background)]
    [InlineData(SchedulePriority.Normal)]
    [InlineData(SchedulePriority.Elevated)]
    [InlineData(SchedulePriority.Critical)]
    public async Task Every_closed_schedule_priority_maps_to_the_exact_queue_priority(SchedulePriority priority)
    {
        var definition = ScheduleEvaluatorTestData.Definition() with { Priority = priority };
        var fixture = Fixture(definition);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        var expected = priority switch
        {
            SchedulePriority.Background => TriggerQueuePriority.Background,
            SchedulePriority.Normal => TriggerQueuePriority.Normal,
            SchedulePriority.Elevated => TriggerQueuePriority.Elevated,
            SchedulePriority.Critical => TriggerQueuePriority.Critical,
            _ => throw new InvalidOperationException("The theory admits only non-default closed priorities."),
        };
        Assert.Equal(expected, Assert.Single(fixture.Queue.Requests).Priority);
    }

    private static async Task AssertCanceledAsync(
        Action<EvaluatorFixture> arrange,
        Func<EvaluatorFixture, CancellationToken> observedToken,
        Func<EvaluatorFixture>? createFixture = null)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = createFixture?.Invoke() ?? Fixture();
        fixture.Store.CancellationSource = cancellation;
        fixture.TimeZone.CancellationSource = cancellation;
        fixture.Overlap.CancellationSource = cancellation;
        fixture.CurrentEvidence.CancellationSource = cancellation;
        fixture.Queue.CancellationSource = cancellation;
        arrange(fixture);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(cancellation.Token, observedToken(fixture));
    }

    [Theory]
    [InlineData(TriggerAdmissionStatus.Expired, TriggerAdmissionReason.DeadlineExceeded)]
    [InlineData(TriggerAdmissionStatus.Expired, TriggerAdmissionReason.Expired)]
    [InlineData(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleLoop)]
    [InlineData(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleAdapter)]
    [InlineData(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.ActorMismatch)]
    [InlineData(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.SurfaceMismatch)]
    [InlineData(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.WorkspaceMismatch)]
    [InlineData(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.RoleMismatch)]
    [InlineData(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.AuthorityMismatch)]
    [InlineData(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleAuthority)]
    [InlineData(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.AuthorityBoundary)]
    [InlineData(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleDelivery)]
    public async Task Conclusive_rejection_admission_evidence_is_finalized_without_retry(
        TriggerAdmissionStatus admissionStatus,
        TriggerAdmissionReason admissionReason)
    {
        var fixture = Fixture();
        fixture.Queue.Status = TriggerQueueAdmissionStatus.Rejected;
        fixture.Queue.ReasonOverride = TriggerQueueAdmissionReason.AdmissionRejected;
        fixture.Queue.AdmissionStatusOverride = admissionStatus;
        fixture.Queue.AdmissionReasonOverride = admissionReason;

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Rejected, result.Status);
        Assert.Equal(ScheduleDeliveryResultKind.Rejected,
            Assert.Single(result.State!.TerminalDeliveryEvidence).Result.Kind);
        Assert.Null(result.State.PendingDelivery);
        Assert.Equal(1, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Conflict_not_yet_eligible_and_admission_unavailable_queue_evidence_remain_coherent()
    {
        var conflict = Fixture();
        conflict.Queue.Status = TriggerQueueAdmissionStatus.Rejected;
        conflict.Queue.ReasonOverride = TriggerQueueAdmissionReason.IdentityConflict;
        conflict.Queue.AdmissionStatusOverride = TriggerAdmissionStatus.Conflicting;
        conflict.Queue.AdmissionReasonOverride = TriggerAdmissionReason.IdentityConflict;
        var conflictResult = await conflict.Evaluator.EvaluateAsync(conflict.Definition.ScheduleId);

        var deferred = Fixture();
        deferred.Queue.Status = TriggerQueueAdmissionStatus.Backpressured;
        deferred.Queue.ReasonOverride = TriggerQueueAdmissionReason.QueueCountExceeded;
        deferred.Queue.AdmissionStatusOverride = TriggerAdmissionStatus.NotYetEligible;
        deferred.Queue.AdmissionReasonOverride = TriggerAdmissionReason.NotBefore;
        var deferredResult = await deferred.Evaluator.EvaluateAsync(deferred.Definition.ScheduleId);

        var unavailable = Fixture();
        unavailable.Queue.Status = TriggerQueueAdmissionStatus.Unavailable;
        unavailable.Queue.ReasonOverride = TriggerQueueAdmissionReason.AdmissionUnavailable;
        unavailable.Queue.AdmissionStatusOverride = TriggerAdmissionStatus.Unavailable;
        unavailable.Queue.AdmissionReasonOverride = TriggerAdmissionReason.AdapterUnavailable;
        var unavailableResult = await unavailable.Evaluator.EvaluateAsync(unavailable.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Rejected, conflictResult.Status);
        Assert.Equal(ScheduleEvaluationStatus.Backpressured, deferredResult.Status);
        Assert.Equal(ScheduleEvaluationStatus.NeedsReview, unavailableResult.Status);
        Assert.Equal(SchedulePendingDeliveryPhase.ResultObserved,
            deferredResult.State!.PendingDelivery!.Phase);
        Assert.Equal(SchedulePendingDeliveryPhase.ResultObserved,
            unavailableResult.State!.PendingDelivery!.Phase);
    }

    [Fact]
    public async Task Persist_failures_at_prepare_observe_and_finalize_preserve_exact_recovery_state()
    {
        var prepare = Fixture();
        prepare.Store.MutationStatusSelector = count => count == 2
            ? ScheduleStoreMutationStatus.Conflict
            : null;
        var prepareResult = await prepare.Evaluator.EvaluateAsync(prepare.Definition.ScheduleId);
        AssertSameState(prepareResult.State, prepare.Store.State);

        var observe = Fixture();
        observe.Store.MutationStatusSelector = count => count == 3
            ? ScheduleStoreMutationStatus.Backpressured
            : null;
        var observeResult = await observe.Evaluator.EvaluateAsync(observe.Definition.ScheduleId);
        AssertSameState(observeResult.State, observe.Store.State);

        var finalize = Fixture();
        finalize.Store.MutationStatusSelector = count => count == 4
            ? ScheduleStoreMutationStatus.Conflict
            : null;
        var finalizeResult = await finalize.Evaluator.EvaluateAsync(finalize.Definition.ScheduleId);
        AssertSameState(finalizeResult.State, finalize.Store.State);
        var recovered = await finalize.Evaluator.EvaluateAsync(finalize.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Conflict, prepareResult.Status);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, prepareResult.State!.PendingDelivery!.Phase);
        Assert.Equal(ScheduleEvaluationStatus.Backpressured, observeResult.Status);
        Assert.Equal(SchedulePendingDeliveryPhase.Prepared, observeResult.State!.PendingDelivery!.Phase);
        Assert.Equal(ScheduleEvaluationStatus.Conflict, finalizeResult.Status);
        Assert.Equal(SchedulePendingDeliveryPhase.ResultObserved, finalizeResult.State!.PendingDelivery!.Phase);
        Assert.Equal(ScheduleEvaluationStatus.Queued, recovered.Status);
        Assert.Null(recovered.State!.PendingDelivery);
        Assert.Equal(1, finalize.Queue.Calls);
    }

    [Fact]
    public async Task Revision_exhaustion_blocks_ambiguous_record_finalization_skip_and_deferral()
    {
        var seed = Fixture();
        _ = await seed.Evaluator.EvaluateAsync(seed.Definition.ScheduleId);
        var preparedState = seed.Store.Mutations[1].Replacement with
        {
            StateRevision = ScheduleContractLimits.MaxRevision,
        };
        var ambiguous = Fixture(seed.Definition, preparedState);
        ambiguous.Queue.Throw = true;
        var ambiguousResult = await ambiguous.Evaluator.EvaluateAsync(seed.Definition.ScheduleId);

        var observedState = seed.Store.Mutations[2].Replacement with
        {
            StateRevision = ScheduleContractLimits.MaxRevision,
        };
        var finalize = Fixture(seed.Definition, observedState);
        var finalizeResult = await finalize.Evaluator.EvaluateAsync(seed.Definition.ScheduleId);

        var claimSeed = Fixture();
        claimSeed.CurrentEvidence.Status = ScheduleCurrentEvidenceStatus.PermissionDenied;
        var claimed = await claimSeed.Evaluator.EvaluateAsync(claimSeed.Definition.ScheduleId);
        var farFuture = ScheduleEvaluatorTestData.FirstUtc.AddDays(31);
        var skipState = claimed.State! with
        {
            StateRevision = ScheduleContractLimits.MaxRevision,
            LastClockObservedAtUtc = farFuture,
        };
        var skip = Fixture(claimSeed.Definition, skipState, farFuture);
        var skipResult = await skip.Evaluator.EvaluateAsync(claimSeed.Definition.ScheduleId);

        var deferState = claimed.State! with
        {
            StateRevision = ScheduleContractLimits.MaxRevision,
        };
        var defer = Fixture(claimSeed.Definition, deferState);
        defer.Overlap.Status = ScheduleOverlapStatus.Active;
        var deferResult = await defer.Evaluator.EvaluateAsync(claimSeed.Definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.BoundExceeded, "state-revision-exhausted"),
            (ambiguousResult.Status, ambiguousResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.BoundExceeded, "schedule-evidence-bound-exceeded"),
            (finalizeResult.Status, finalizeResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.BoundExceeded, "schedule-evidence-bound-exceeded"),
            (skipResult.Status, skipResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.BoundExceeded, "state-revision-exhausted"),
            (deferResult.Status, deferResult.ReasonCode));
        Assert.Empty(ambiguous.Store.Mutations);
        Assert.Empty(finalize.Store.Mutations);
        Assert.Empty(skip.Store.Mutations);
        Assert.Empty(defer.Store.Mutations);
    }

    [Fact]
    public async Task Revision_exhaustion_after_queue_observation_retains_the_prepared_recovery_checkpoint()
    {
        var seed = Fixture();
        _ = await seed.Evaluator.EvaluateAsync(seed.Definition.ScheduleId);
        var preparedState = seed.Store.Mutations[1].Replacement with
        {
            StateRevision = ScheduleContractLimits.MaxRevision,
        };
        var fixture = Fixture(seed.Definition, preparedState);

        var result = await fixture.Evaluator.EvaluateAsync(seed.Definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.BoundExceeded, "state-revision-exhausted"),
            (result.Status, result.ReasonCode));
        Assert.Equal(SchedulePendingDeliveryPhase.Prepared, result.State!.PendingDelivery!.Phase);
        Assert.Equal(1, fixture.Queue.Calls);
        Assert.Empty(fixture.Store.Mutations);
    }

    [Fact]
    public async Task Active_catch_up_scan_failure_closes_the_crossed_horizon_without_rewriting_the_episode()
    {
        var definition = ScheduleEvaluatorTestData.Definition(catchUpLimit: 2);
        var fixture = Fixture(
            definition,
            now: ScheduleEvaluatorTestData.FirstUtc.AddDays(2).AddHours(1));
        var first = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);
        Assert.Equal(new ScheduleCatchUpEpisode(1, 3, 1), first.State!.CatchUpEpisode);

        fixture.TimeProvider.Now = ScheduleEvaluatorTestData.FirstUtc.AddDays(35);
        fixture.TimeZone.LocalResolver = (timeZone, local) =>
            local == definition.Recurrence.FirstLocalOccurrence.AddDays(3)
                ? new ScheduleTimeZoneResolution(
                    ScheduleTimeZoneResolutionStatus.Unavailable,
                    timeZone.RulesFingerprint,
                    local,
                    null,
                    null)
                : new ScheduleTimeZoneResolution(
                    ScheduleTimeZoneResolutionStatus.Unique,
                    timeZone.RulesFingerprint,
                    local,
                    new DateTimeOffset(local.AddHours(5), TimeSpan.Zero),
                    null);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.Unavailable, "time-zone-unavailable"),
            (result.Status, result.ReasonCode));
        Assert.Equal(new ScheduleCatchUpEpisode(1, 3, 1), result.State!.CatchUpEpisode);
        Assert.Equal(1, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Fixed_catch_up_closes_when_later_range_or_future_evidence_is_unavailable()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            intervalSeconds: 60,
            catchUpLimit: 1);
        var now = ScheduleEvaluatorTestData.FirstUtc.AddMinutes(5);

        var lastRange = Fixture(definition, now: now);
        lastRange.TimeZone.InstantResolver = (timeZone, utc) => utc == ScheduleEvaluatorTestData.FirstUtc.AddMinutes(5)
            ? new ScheduleInstantResolution(ScheduleInstantResolutionStatus.Unavailable, timeZone.RulesFingerprint, default)
            : ResolvedInstant(timeZone, utc);
        var lastRangeResult = await lastRange.Evaluator.EvaluateAsync(definition.ScheduleId);

        var future = Fixture(definition, now: now);
        future.TimeZone.InstantResolver = (timeZone, utc) => utc == ScheduleEvaluatorTestData.FirstUtc.AddMinutes(6)
            ? new ScheduleInstantResolution(ScheduleInstantResolutionStatus.Unavailable, timeZone.RulesFingerprint, default)
            : ResolvedInstant(timeZone, utc);
        var futureResult = await future.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.Unavailable, "time-zone-unavailable"),
            (lastRangeResult.Status, lastRangeResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.Unavailable, "time-zone-unavailable"),
            (futureResult.Status, futureResult.ReasonCode));
        Assert.Equal(0, lastRange.Queue.Calls);
        Assert.Equal(0, future.Queue.Calls);
    }

    [Fact]
    public async Task Recurrence_exhaustion_at_the_supported_year_boundary_is_closed_without_arithmetic_overflow()
    {
        var firstLocal = new DateTime(9998, 12, 31, 9, 0, 0, DateTimeKind.Unspecified);
        var firstUtc = new DateTimeOffset(9998, 12, 31, 14, 0, 0, TimeSpan.Zero);
        var dailyDefinition = ScheduleEvaluatorTestData.Definition(firstLocal: firstLocal);
        var daily = Fixture(
            dailyDefinition,
            ScheduleEvaluatorTestData.State(
                dailyDefinition,
                ScheduleEvaluatorTestData.Occurrence(local: firstLocal, utc: firstUtc, timeZone: dailyDefinition.TimeZone)),
            firstUtc.AddHours(1));

        var dailyResult = await daily.Evaluator.EvaluateAsync(dailyDefinition.ScheduleId);

        var fixedDefinition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            firstLocal: firstLocal,
            intervalSeconds: 24 * 60 * 60);
        var fixedInterval = Fixture(
            fixedDefinition,
            ScheduleEvaluatorTestData.State(
                fixedDefinition,
                ScheduleEvaluatorTestData.Occurrence(local: firstLocal, utc: firstUtc, timeZone: fixedDefinition.TimeZone)),
            firstUtc.AddHours(1));

        var fixedResult = await fixedInterval.Evaluator.EvaluateAsync(fixedDefinition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, dailyResult.Status);
        Assert.Null(dailyResult.State!.NextOccurrence);
        Assert.Equal(ScheduleEvaluationStatus.Queued, fixedResult.Status);
        Assert.Null(fixedResult.State!.NextOccurrence);
        Assert.Equal(1, fixedInterval.Queue.Calls);
    }

    [Fact]
    public async Task Current_fixed_interval_null_and_exception_evidence_fail_closed()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            intervalSeconds: 60);
        var occurrence = ScheduleEvaluatorTestData.Occurrence(
            ordinal: 2,
            local: ScheduleEvaluatorTestData.FirstLocal.AddMinutes(1),
            utc: ScheduleEvaluatorTestData.FirstUtc.AddMinutes(1),
            timeZone: definition.TimeZone);
        var state = ScheduleEvaluatorTestData.State(definition, occurrence);

        var nullFixture = Fixture(definition, state);
        nullFixture.TimeZone.ReturnNullInstant = true;
        var nullResult = await nullFixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        var throwing = Fixture(definition, state);
        throwing.TimeZone.ThrowOnInstantResolution = true;
        var throwingResult = await throwing.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.Corrupt, "time-zone-evidence-invalid"),
            (nullResult.Status, nullResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.Unavailable, "time-zone-unavailable"),
            (throwingResult.Status, throwingResult.ReasonCode));
        Assert.Equal(0, nullFixture.Queue.Calls);
        Assert.Equal(0, throwing.Queue.Calls);
    }

    [Fact]
    public async Task Recurrence_scan_probe_bound_fails_closed_without_queueing()
    {
        var definition = ScheduleEvaluatorTestData.Definition(catchUpLimit: 1);
        var fixture = Fixture(definition, now: ScheduleEvaluatorTestData.FirstUtc.AddMinutes(20));
        fixture.TimeZone.LocalResolver = (timeZone, local) =>
        {
            var ordinalOffset = (long)(local - definition.Recurrence.FirstLocalOccurrence).TotalDays;
            return new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                timeZone.RulesFingerprint,
                local,
                ScheduleEvaluatorTestData.FirstUtc.AddSeconds(ordinalOffset),
                null);
        };

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.BoundExceeded, result.Status);
        Assert.Equal("recurrence-probe-bound-exceeded", result.ReasonCode);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Recurrence_proof_bound_fails_closed_when_the_final_probe_finds_the_first_future_occurrence()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            catchUpLimit: ScheduleContractLimits.MaxCatchUpOccurrences);
        var now = ScheduleEvaluatorTestData.FirstUtc.AddSeconds(
            ScheduleContractLimits.MaxFinalizationEvidenceItems);
        var fixture = Fixture(definition, now: now);
        fixture.TimeZone.LocalResolver = (timeZone, local) =>
        {
            var ordinalOffset = (long)(local - definition.Recurrence.FirstLocalOccurrence).TotalDays;
            return new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                timeZone.RulesFingerprint,
                local,
                ScheduleEvaluatorTestData.FirstUtc.AddSeconds(ordinalOffset),
                null);
        };

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.BoundExceeded, result.Status);
        Assert.Equal("recurrence-evidence-bound-exceeded", result.ReasonCode);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Equal(ScheduleContractLimits.MaxFinalizationEvidenceItems + 2, fixture.TimeZone.LocalCalls);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Recurrence_skip_evidence_bound_fails_closed_without_queueing()
    {
        var firstLocal = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Unspecified);
        var firstUtc = new DateTimeOffset(2026, 1, 1, 14, 0, 0, TimeSpan.Zero);
        var definition = ScheduleEvaluatorTestData.Definition(
            firstLocal: firstLocal,
            catchUpLimit: 1,
            invalidLocal: ScheduleInvalidLocalTimePolicy.Skip);
        var fixture = Fixture(
            definition,
            ScheduleEvaluatorTestData.State(
                definition,
                ScheduleEvaluatorTestData.Occurrence(local: firstLocal, utc: firstUtc, timeZone: definition.TimeZone)),
            firstUtc.AddMinutes(20));
        fixture.TimeZone.LocalResolver = (timeZone, local) =>
        {
            var ordinalOffset = (int)(local - firstLocal).TotalDays;
            return ordinalOffset % 2 == 1
                ? new ScheduleTimeZoneResolution(
                    ScheduleTimeZoneResolutionStatus.InvalidLocalTime,
                    timeZone.RulesFingerprint,
                    local.AddMinutes(30),
                    firstUtc.AddSeconds(ordinalOffset),
                    null)
                : new ScheduleTimeZoneResolution(
                    ScheduleTimeZoneResolutionStatus.Unique,
                    timeZone.RulesFingerprint,
                    local,
                    firstUtc.AddSeconds(ordinalOffset),
                    null);
        };

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.BoundExceeded, result.Status);
        Assert.Equal("recurrence-evidence-bound-exceeded", result.ReasonCode);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Malformed_available_current_evidence_fields_fail_closed_before_queueing()
    {
        var mutations = new Func<ScheduleCurrentEvidence, ScheduleCurrentEvidence>[]
        {
            evidence => CopyEvidence(evidence, evidenceHash: "invalid"),
            evidence => CopyEvidence(
                evidence,
                observedAtUtc: evidence.ObservedAtUtc.AddTicks(-1),
                authority: TriggerAdmissionTestData.Authority(
                    evaluatedAtUtc: evidence.ObservedAtUtc.AddTicks(-1))),
            evidence => CopyEvidence(evidence, target: TriggerAdmissionTestData.GovernedLoop(graphId: "other-loop")),
            evidence => CopyEvidence(evidence, adapter: TriggerAdmissionTestData.Adapter(implementation: "other/time")),
            evidence => CopyEvidence(evidence, actor: TriggerAdmissionTestData.ActorContext(actor: "other-actor", surface: "scheduler")),
            evidence => CopyEvidence(evidence, actor: TriggerAdmissionTestData.ActorContext(surface: "other-surface")),
            evidence => CopyEvidence(evidence, actor: TriggerAdmissionTestData.ActorContext(surface: "scheduler", workspace: "other-workspace")),
            evidence => CopyEvidence(evidence, actor: TriggerAdmissionTestData.ActorContext(surface: "scheduler", role: "other-role")),
            evidence => CopyEvidence(evidence, authority: TriggerAdmissionTestData.Authority(evaluatedAtUtc: evidence.ObservedAtUtc, profileIdText: "other-profile")),
            evidence => CopyEvidence(evidence, authority: TriggerAdmissionTestData.Authority(evaluatedAtUtc: evidence.ObservedAtUtc.AddTicks(1))),
            evidence => CopyEvidence(evidence, recurrencePermitted: false),
            evidence => CopyEvidence(evidence, payload: [9, 9, 9]),
        };

        foreach (var mutate in mutations)
        {
            var fixture = Fixture();
            fixture.CurrentEvidence.EvidenceMutation = mutate;

            var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

            Assert.Equal(ScheduleEvaluationStatus.Corrupt, result.Status);
            Assert.Equal("schedule-evidence-corrupt", result.ReasonCode);
            Assert.Equal(0, fixture.Queue.Calls);
        }
    }

    [Theory]
    [InlineData(ScheduleTimeZoneResolutionStatus.Unique)]
    [InlineData(ScheduleTimeZoneResolutionStatus.AmbiguousLocalTime)]
    [InlineData(ScheduleTimeZoneResolutionStatus.InvalidLocalTime)]
    public async Task Malformed_current_local_resolution_shapes_fail_closed(
        ScheduleTimeZoneResolutionStatus status)
    {
        var fixture = Fixture();
        fixture.TimeZone.LocalResolver = (timeZone, local) => status switch
        {
            ScheduleTimeZoneResolutionStatus.Unique => new(
                status,
                timeZone.RulesFingerprint,
                local.AddMinutes(1),
                ScheduleEvaluatorTestData.FirstUtc,
                null),
            ScheduleTimeZoneResolutionStatus.AmbiguousLocalTime => new(
                status,
                timeZone.RulesFingerprint,
                local,
                ScheduleEvaluatorTestData.FirstUtc,
                ScheduleEvaluatorTestData.FirstUtc.AddHours(-1)),
            _ => new(
                status,
                timeZone.RulesFingerprint,
                local.AddMinutes(-1),
                ScheduleEvaluatorTestData.FirstUtc,
                null),
        };

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, result.Status);
        Assert.Equal("time-zone-rules-mismatch", result.ReasonCode);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Theory]
    [InlineData(ScheduleTimeZoneResolutionStatus.Unique)]
    [InlineData(ScheduleTimeZoneResolutionStatus.AmbiguousLocalTime)]
    [InlineData(ScheduleTimeZoneResolutionStatus.InvalidLocalTime)]
    public async Task Malformed_successor_local_resolution_shapes_fail_closed(
        ScheduleTimeZoneResolutionStatus status)
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        var fixture = Fixture(definition);
        fixture.TimeZone.LocalResolver = (timeZone, local) => local == definition.Recurrence.FirstLocalOccurrence
            ? new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                timeZone.RulesFingerprint,
                local,
                ScheduleEvaluatorTestData.FirstUtc,
                null)
            : status switch
            {
                ScheduleTimeZoneResolutionStatus.Unique => new(
                    status,
                    timeZone.RulesFingerprint,
                    local.AddMinutes(1),
                    ScheduleEvaluatorTestData.FirstUtc.AddDays(1),
                    null),
                ScheduleTimeZoneResolutionStatus.AmbiguousLocalTime => new(
                    status,
                    timeZone.RulesFingerprint,
                    local,
                    ScheduleEvaluatorTestData.FirstUtc.AddDays(1),
                    ScheduleEvaluatorTestData.FirstUtc.AddDays(1).AddHours(-1)),
                _ => new(
                    status,
                    timeZone.RulesFingerprint,
                    local.AddMinutes(-1),
                    ScheduleEvaluatorTestData.FirstUtc.AddDays(1),
                    null),
            };

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, result.Status);
        Assert.Equal("time-zone-evidence-invalid", result.ReasonCode);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Malformed_fixed_interval_instant_shape_fails_closed()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            intervalSeconds: 60);
        var fixture = Fixture(definition);
        fixture.TimeZone.InstantResolver = (timeZone, _) => new ScheduleInstantResolution(
            ScheduleInstantResolutionStatus.Resolved,
            timeZone.RulesFingerprint,
            DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Unspecified));

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, result.Status);
        Assert.Equal("time-zone-evidence-invalid", result.ReasonCode);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Unique_local_evidence_with_a_second_utc_candidate_fails_closed_at_each_position()
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        var current = Fixture(definition);
        current.TimeZone.LocalResolver = (timeZone, local) => new ScheduleTimeZoneResolution(
            ScheduleTimeZoneResolutionStatus.Unique,
            timeZone.RulesFingerprint,
            local,
            new DateTimeOffset(local.AddHours(5), TimeSpan.Zero),
            new DateTimeOffset(local.AddHours(6), TimeSpan.Zero));
        var currentResult = await current.Evaluator.EvaluateAsync(definition.ScheduleId);

        var successor = Fixture(definition);
        successor.TimeZone.LocalResolver = (timeZone, local) => new ScheduleTimeZoneResolution(
            ScheduleTimeZoneResolutionStatus.Unique,
            timeZone.RulesFingerprint,
            local,
            new DateTimeOffset(local.AddHours(5), TimeSpan.Zero),
            local == definition.Recurrence.FirstLocalOccurrence
                ? null
                : new DateTimeOffset(local.AddHours(6), TimeSpan.Zero));
        var successorResult = await successor.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.Corrupt, "time-zone-rules-mismatch"),
            (currentResult.Status, currentResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.Corrupt, "time-zone-evidence-invalid"),
            (successorResult.Status, successorResult.ReasonCode));
        Assert.Equal(0, current.Queue.Calls);
        Assert.Equal(0, successor.Queue.Calls);
    }

    [Fact]
    public async Task Null_or_mismatched_successor_time_zone_evidence_fails_closed_before_queueing()
    {
        var dailyDefinition = ScheduleEvaluatorTestData.Definition();
        var nullLocal = Fixture(dailyDefinition);
        nullLocal.TimeZone.ReturnNullLocalCall = 2;
        var nullLocalResult = await nullLocal.Evaluator.EvaluateAsync(dailyDefinition.ScheduleId);

        var mismatchedRules = Fixture(dailyDefinition);
        mismatchedRules.TimeZone.LocalResolver = (timeZone, local) => new ScheduleTimeZoneResolution(
            ScheduleTimeZoneResolutionStatus.Unique,
            local == dailyDefinition.Recurrence.FirstLocalOccurrence ? timeZone.RulesFingerprint : new string('b', 64),
            local,
            new DateTimeOffset(local.AddHours(5), TimeSpan.Zero),
            null);
        var mismatchedRulesResult = await mismatchedRules.Evaluator.EvaluateAsync(dailyDefinition.ScheduleId);

        var fixedDefinition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            intervalSeconds: 60);
        var nullInstant = Fixture(fixedDefinition);
        nullInstant.TimeZone.ReturnNullInstant = true;
        var nullInstantResult = await nullInstant.Evaluator.EvaluateAsync(fixedDefinition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.Corrupt, "time-zone-evidence-invalid"),
            (nullLocalResult.Status, nullLocalResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.Corrupt, "time-zone-rules-mismatch"),
            (mismatchedRulesResult.Status, mismatchedRulesResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.Corrupt, "time-zone-evidence-invalid"),
            (nullInstantResult.Status, nullInstantResult.ReasonCode));
        Assert.Equal(0, nullLocal.Queue.Calls);
        Assert.Equal(0, mismatchedRules.Queue.Calls);
        Assert.Equal(0, nullInstant.Queue.Calls);
    }

    [Fact]
    public async Task Contradictory_overlap_and_unknown_queue_evidence_are_never_treated_as_success()
    {
        var overlap = Fixture();
        overlap.Overlap.EvidenceHashOverride = "invalid";
        var overlapResult = await overlap.Evaluator.EvaluateAsync(overlap.Definition.ScheduleId);

        var queue = Fixture();
        queue.Queue.Status = TriggerQueueAdmissionStatus.ImmediateRejected;
        var queueResult = await queue.Evaluator.EvaluateAsync(queue.Definition.ScheduleId);

        var admission = Fixture();
        admission.Queue.AdmissionStatusOverride = TriggerAdmissionStatus.Unknown;
        admission.Queue.AdmissionReasonOverride = TriggerAdmissionReason.Unknown;
        var admissionResult = await admission.Evaluator.EvaluateAsync(admission.Definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.Corrupt, "overlap-evidence-invalid"),
            (overlapResult.Status, overlapResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.NeedsReview, "queue-evidence-conflict"),
            (queueResult.Status, queueResult.ReasonCode));
        Assert.Equal((ScheduleEvaluationStatus.NeedsReview, "queue-evidence-conflict"),
            (admissionResult.Status, admissionResult.ReasonCode));
    }

    [Fact]
    public async Task Malformed_queue_reason_or_identity_is_durably_ambiguous()
    {
        var malformedReason = Fixture();
        malformedReason.Queue.ReasonOverride = TriggerQueueAdmissionReason.StorageUnavailable;
        var reasonResult = await malformedReason.Evaluator.EvaluateAsync(malformedReason.Definition.ScheduleId);

        var malformedDeliveryIdentity = Fixture();
        malformedDeliveryIdentity.Queue.SubstituteDeliveryIdentity = true;
        var deliveryIdentityResult = await malformedDeliveryIdentity.Evaluator.EvaluateAsync(
            malformedDeliveryIdentity.Definition.ScheduleId);

        var malformedDeduplicationIdentity = Fixture();
        malformedDeduplicationIdentity.Queue.SubstituteDeduplicationIdentity = true;
        var deduplicationIdentityResult = await malformedDeduplicationIdentity.Evaluator.EvaluateAsync(
            malformedDeduplicationIdentity.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.NeedsReview, reasonResult.Status);
        Assert.Equal("queue-evidence-conflict", reasonResult.ReasonCode);
        Assert.Equal(ScheduleEvaluationStatus.NeedsReview, deliveryIdentityResult.Status);
        Assert.Equal("queue-evidence-conflict", deliveryIdentityResult.ReasonCode);
        Assert.Equal(ScheduleEvaluationStatus.NeedsReview, deduplicationIdentityResult.Status);
        Assert.Equal("queue-evidence-conflict", deduplicationIdentityResult.ReasonCode);
        Assert.Equal(ScheduleDeliveryResultKind.Ambiguous,
            reasonResult.State!.PendingDelivery!.Result!.Kind);
        Assert.Equal(ScheduleDeliveryResultKind.Ambiguous,
            deliveryIdentityResult.State!.PendingDelivery!.Result!.Kind);
        Assert.Equal(ScheduleDeliveryResultKind.Ambiguous,
            deduplicationIdentityResult.State!.PendingDelivery!.Result!.Kind);
    }

    [Fact]
    public async Task Prepared_delivery_fails_closed_when_fresh_evidence_crosses_the_temporal_horizon()
    {
        var fixture = Fixture();
        fixture.CurrentEvidence.ObservationDelay = TriggerDeliveryLimits.MaxTemporalHorizon;

        var result = await fixture.Evaluator.EvaluateAsync(fixture.Definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.Corrupt, "prepared-delivery-invalid"),
            (result.Status, result.ReasonCode));
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Single(fixture.Store.Mutations);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Active_fixed_catch_up_requires_a_resolvable_successor_within_supported_time()
    {
        var firstLocal = new DateTime(9998, 12, 31, 9, 0, 0, DateTimeKind.Unspecified);
        var firstUtc = new DateTimeOffset(9998, 12, 31, 14, 0, 0, TimeSpan.Zero);
        var definition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            firstLocal: firstLocal,
            intervalSeconds: 24 * 60 * 60,
            catchUpLimit: 2);
        var current = ScheduleEvaluatorTestData.Occurrence(
            local: firstLocal,
            utc: firstUtc,
            timeZone: definition.TimeZone);
        var state = ScheduleEvaluatorTestData.State(
            definition,
            current,
            catchUp: new ScheduleCatchUpEpisode(1, 2, 2));
        var fixture = Fixture(definition, state, firstUtc.AddHours(1));

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.Corrupt, "catch-up-episode-invalid"),
            (result.Status, result.ReasonCode));
        Assert.Equal(new ScheduleCatchUpEpisode(1, 2, 2), result.State!.CatchUpEpisode);
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State.PendingDelivery!.Phase);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Fixed_catch_up_at_the_current_latest_ordinal_reuses_the_first_future_step()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            intervalSeconds: 24 * 60 * 60,
            catchUpLimit: 1);
        var state = ScheduleEvaluatorTestData.State(
            definition,
            catchUp: new ScheduleCatchUpEpisode(1, 1, 1));
        var fixture = Fixture(definition, state, ScheduleEvaluatorTestData.FirstUtc.AddHours(1));

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        Assert.Equal(2, result.State!.NextOccurrence!.Ordinal);
        Assert.Equal(ScheduleEvaluatorTestData.FirstUtc.AddDays(1), result.State.NextOccurrence.ScheduledAtUtc);
        Assert.Null(result.State.CatchUpEpisode);
        Assert.Equal(1, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Fixed_catch_up_ordinal_ceiling_never_requests_an_impossible_instant()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            recurrence: ScheduleRecurrenceKind.FixedInterval,
            intervalSeconds: 60,
            catchUpLimit: 1);
        var current = ScheduleEvaluatorTestData.Occurrence(
            ordinal: ScheduleContractLimits.MaxOccurrenceOrdinal - 1,
            timeZone: definition.TimeZone);
        var state = ScheduleEvaluatorTestData.State(
            definition,
            current,
            catchUp: new ScheduleCatchUpEpisode(1, ScheduleContractLimits.MaxOccurrenceOrdinal, 1));
        var fixture = Fixture(definition, state, ScheduleEvaluatorTestData.FirstUtc.AddMinutes(2));

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.Corrupt, "prepared-delivery-invalid"),
            (result.Status, result.ReasonCode));
        Assert.Equal(
            [ScheduleEvaluatorTestData.FirstUtc, ScheduleEvaluatorTestData.FirstUtc.AddMinutes(1)],
            fixture.TimeZone.InstantRequests);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Active_daily_catch_up_fails_closed_when_its_required_successor_cannot_exist()
    {
        var firstLocal = new DateTime(9998, 12, 30, 9, 0, 0, DateTimeKind.Unspecified);
        var currentLocal = firstLocal.AddDays(1);
        var currentUtc = new DateTimeOffset(9998, 12, 31, 14, 0, 0, TimeSpan.Zero);
        var definition = ScheduleEvaluatorTestData.Definition(firstLocal: firstLocal, catchUpLimit: 2);
        var current = ScheduleEvaluatorTestData.Occurrence(
            ordinal: 2,
            local: currentLocal,
            utc: currentUtc,
            timeZone: definition.TimeZone);
        var state = ScheduleEvaluatorTestData.State(
            definition,
            current,
            catchUp: new ScheduleCatchUpEpisode(1, 3, 2));
        var fixture = Fixture(definition, state, currentUtc.AddHours(1));

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.Corrupt, "catch-up-episode-invalid"),
            (result.Status, result.ReasonCode));
        Assert.Equal(new ScheduleCatchUpEpisode(1, 3, 2), result.State!.CatchUpEpisode);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Active_daily_catch_up_retains_skips_before_its_next_admitted_occurrence()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            catchUpLimit: 2,
            invalidLocal: ScheduleInvalidLocalTimePolicy.Skip);
        var state = ScheduleEvaluatorTestData.State(
            definition,
            catchUp: new ScheduleCatchUpEpisode(1, 3, 2));
        var now = ScheduleEvaluatorTestData.FirstUtc.AddMinutes(5);
        var fixture = Fixture(definition, state, now);
        fixture.TimeZone.LocalResolver = (timeZone, local) =>
        {
            var ordinal = (int)(local - definition.Recurrence.FirstLocalOccurrence).TotalDays + 1;
            return ordinal == 2
                ? new ScheduleTimeZoneResolution(
                    ScheduleTimeZoneResolutionStatus.InvalidLocalTime,
                    timeZone.RulesFingerprint,
                    local.AddMinutes(30),
                    ScheduleEvaluatorTestData.FirstUtc.AddMinutes(1),
                    null)
                : new ScheduleTimeZoneResolution(
                    ScheduleTimeZoneResolutionStatus.Unique,
                    timeZone.RulesFingerprint,
                    local,
                    ScheduleEvaluatorTestData.FirstUtc.AddMinutes(ordinal - 1),
                    null);
        };

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        Assert.Equal(3, result.State!.NextOccurrence!.Ordinal);
        Assert.Equal(new ScheduleCatchUpEpisode(1, 3, 1), result.State.CatchUpEpisode);
        var skipped = Assert.Single(result.State.DispositionEvidence);
        Assert.Equal(ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped, skipped.Disposition);
        Assert.Equal(2, skipped.FirstOrdinal);
    }

    [Fact]
    public async Task Initial_daily_catch_up_retains_the_last_supported_due_occurrence()
    {
        var firstLocal = new DateTime(9998, 12, 30, 9, 0, 0, DateTimeKind.Unspecified);
        var firstUtc = new DateTimeOffset(9998, 12, 30, 14, 0, 0, TimeSpan.Zero);
        var definition = ScheduleEvaluatorTestData.Definition(firstLocal: firstLocal, catchUpLimit: 2);
        var current = ScheduleEvaluatorTestData.Occurrence(
            local: firstLocal,
            utc: firstUtc,
            timeZone: definition.TimeZone);
        var state = ScheduleEvaluatorTestData.State(definition, current);
        var fixture = Fixture(definition, state, firstUtc.AddDays(1).AddHours(1));

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
        Assert.Equal(2, result.State!.NextOccurrence!.Ordinal);
        Assert.Equal(new ScheduleCatchUpEpisode(1, 2, 1), result.State.CatchUpEpisode);
        Assert.Equal(1, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Initial_daily_catch_up_fails_closed_when_a_later_due_resolution_is_unavailable()
    {
        var definition = ScheduleEvaluatorTestData.Definition(catchUpLimit: 2);
        var fixture = Fixture(
            definition,
            now: ScheduleEvaluatorTestData.FirstUtc.AddDays(2));
        fixture.TimeZone.LocalResolver = (timeZone, local) =>
            local == definition.Recurrence.FirstLocalOccurrence.AddDays(2)
                ? new ScheduleTimeZoneResolution(
                    ScheduleTimeZoneResolutionStatus.Unavailable,
                    timeZone.RulesFingerprint,
                    local,
                    null,
                    null)
                : new ScheduleTimeZoneResolution(
                    ScheduleTimeZoneResolutionStatus.Unique,
                    timeZone.RulesFingerprint,
                    local,
                    new DateTimeOffset(local.AddHours(5), TimeSpan.Zero),
                    null);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.Unavailable, "time-zone-unavailable"),
            (result.Status, result.ReasonCode));
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    [Fact]
    public async Task Successor_resolution_probe_bound_fails_closed_after_only_skippable_local_times()
    {
        var definition = ScheduleEvaluatorTestData.Definition(
            invalidLocal: ScheduleInvalidLocalTimePolicy.Skip);
        var fixture = Fixture(definition);
        fixture.TimeZone.LocalResolver = (timeZone, local) =>
            local == definition.Recurrence.FirstLocalOccurrence
                ? new ScheduleTimeZoneResolution(
                    ScheduleTimeZoneResolutionStatus.Unique,
                    timeZone.RulesFingerprint,
                    local,
                    ScheduleEvaluatorTestData.FirstUtc,
                    null)
                : new ScheduleTimeZoneResolution(
                    ScheduleTimeZoneResolutionStatus.InvalidLocalTime,
                    timeZone.RulesFingerprint,
                    local.AddMinutes(30),
                    ScheduleEvaluatorTestData.Now,
                    null);

        var result = await fixture.Evaluator.EvaluateAsync(definition.ScheduleId);

        Assert.Equal((ScheduleEvaluationStatus.BoundExceeded, "recurrence-probe-bound-exceeded"),
            (result.Status, result.ReasonCode));
        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, result.State!.PendingDelivery!.Phase);
        Assert.Equal(ScheduleContractLimits.MaxFinalizationEvidenceItems + 2, fixture.TimeZone.LocalCalls);
        Assert.Equal(0, fixture.Queue.Calls);
    }

    private static ScheduleCurrentEvidence CopyEvidence(
        ScheduleCurrentEvidence evidence,
        string? evidenceHash = null,
        DateTimeOffset? observedAtUtc = null,
        TriggerLoopReference? target = null,
        TriggerAdapterReference? adapter = null,
        TriggerActorContext? actor = null,
        TriggerAuthorityEvidence? authority = null,
        bool? recurrencePermitted = null,
        byte[]? payload = null)
    {
        Assert.True(evidence.TryGetResolvedPayload(out var retainedPayload));
        return new ScheduleCurrentEvidence(
            evidenceHash ?? evidence.EvidenceHash,
            observedAtUtc ?? evidence.ObservedAtUtc,
            target ?? evidence.Target,
            adapter ?? evidence.Adapter,
            actor ?? evidence.ActorContext,
            authority ?? evidence.Authority,
            recurrencePermitted ?? evidence.RecurrencePermitted,
            payload ?? retainedPayload!);
    }

    private static ScheduleInstantResolution ResolvedInstant(
        ScheduleTimeZoneReference timeZone,
        DateTimeOffset utc)
        => new(
            ScheduleInstantResolutionStatus.Resolved,
            timeZone.RulesFingerprint,
            DateTime.SpecifyKind(utc.UtcDateTime.AddHours(-5), DateTimeKind.Unspecified));

    private static void AssertSameState(ScheduleState? actual, ScheduleState expected)
    {
        Assert.NotNull(actual);
        Assert.True(ScheduleContractHash.TryComputeState(actual, out var actualHash, out var actualValidation),
            ScheduleEvaluatorTestData.Errors(actualValidation));
        Assert.True(ScheduleContractHash.TryComputeState(expected, out var expectedHash, out var expectedValidation),
            ScheduleEvaluatorTestData.Errors(expectedValidation));
        Assert.Equal(expectedHash, actualHash);
    }

    private static EvaluatorFixture Fixture(
        ScheduleDefinition? definition = null,
        ScheduleState? state = null,
        DateTimeOffset? now = null)
    {
        definition ??= ScheduleEvaluatorTestData.Definition();
        state ??= ScheduleEvaluatorTestData.State(definition);
        var store = new TestScheduleStore(definition, state);
        var evidence = new TestScheduleCurrentEvidence();
        var overlap = new TestScheduleOverlap();
        var timeZone = new TestScheduleTimeZone();
        var queue = new TestScheduleQueue();
        var timeProvider = new TestScheduleTimeProvider(now ?? ScheduleEvaluatorTestData.Now);
        return new EvaluatorFixture(
            definition,
            store,
            evidence,
            overlap,
            timeZone,
            queue,
            timeProvider,
            new ScheduleDueOccurrenceEvaluator(store, evidence, overlap, timeZone, queue, timeProvider));
    }

    private static RealQueueEvaluatorFixture RealQueueFixture(
        ScheduleDefinition definition,
        ScheduleState state,
        DateTimeOffset now,
        TriggerDeliveryAdmissionHistoryEntry? history = null)
    {
        var store = new TestScheduleStore(definition, state);
        var evidence = new TestScheduleCurrentEvidence();
        var overlap = new TestScheduleOverlap();
        var timeZone = new TestScheduleTimeZone();
        var queueMutation = new TestScheduleQueueMutation();
        var queue = new TriggerQueueAdmissionService(
            new TriggerDeliveryAdmissionService(new TestScheduleAdmissionHistory(history)),
            queueMutation);
        var timeProvider = new TestScheduleTimeProvider(now);
        return new RealQueueEvaluatorFixture(
            store,
            evidence,
            queueMutation,
            new ScheduleDueOccurrenceEvaluator(
                store,
                evidence,
                overlap,
                timeZone,
                queue,
                timeProvider));
    }

    private static void AssertLegalTransitions(
        ScheduleDefinition definition,
        IReadOnlyList<ScheduleStateCompareExchange> mutations)
    {
        Assert.NotEmpty(mutations);
        foreach (var mutation in mutations)
        {
            var validation = ScheduleStateTransitionValidator.Validate(
                definition,
                mutation.Expected,
                mutation.Replacement);
            Assert.True(validation.IsValid, ScheduleEvaluatorTestData.Errors(validation));
        }
    }

    private sealed record EvaluatorFixture(
        ScheduleDefinition Definition,
        TestScheduleStore Store,
        TestScheduleCurrentEvidence CurrentEvidence,
        TestScheduleOverlap Overlap,
        TestScheduleTimeZone TimeZone,
        TestScheduleQueue Queue,
        TestScheduleTimeProvider TimeProvider,
        ScheduleDueOccurrenceEvaluator Evaluator);

    private sealed record RealQueueEvaluatorFixture(
        TestScheduleStore Store,
        TestScheduleCurrentEvidence CurrentEvidence,
        TestScheduleQueueMutation QueueMutation,
        ScheduleDueOccurrenceEvaluator Evaluator);
}
