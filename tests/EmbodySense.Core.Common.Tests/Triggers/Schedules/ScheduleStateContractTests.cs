using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Tests.Triggers.Schedules;

public sealed class ScheduleStateContractTests
{
    [Fact]
    public void Pending_phase_matrix_supports_crash_safe_claim_prepare_and_result_observation()
    {
        var claimed = ScheduleContractTestData.Pending();
        AssertPendingValid(claimed);

        var preparedEvidence = ScheduleContractTestData.Prepared();
        var prepared = ScheduleContractTestData.Pending(prepared: preparedEvidence);
        AssertPendingValid(prepared);

        var result = ScheduleContractTestData.Result(preparedEvidence.CanonicalEnvelopeHash);
        var observed = ScheduleContractTestData.Pending(prepared: preparedEvidence, result: result);
        AssertPendingValid(observed);

        Assert.Equal(SchedulePendingDeliveryPhase.Claimed, claimed.Phase);
        Assert.Equal(SchedulePendingDeliveryPhase.Prepared, prepared.Phase);
        Assert.Equal(SchedulePendingDeliveryPhase.ResultObserved, observed.Phase);
    }

    [Fact]
    public void Pending_phase_matrix_rejects_every_hybrid_or_partial_shape()
    {
        var claimed = ScheduleContractTestData.Pending();
        var preparedEvidence = ScheduleContractTestData.Prepared();
        var prepared = ScheduleContractTestData.Pending(prepared: preparedEvidence);
        var result = ScheduleContractTestData.Result(preparedEvidence.CanonicalEnvelopeHash);

        AssertPendingInvalid(prepared with { Phase = SchedulePendingDeliveryPhase.Claimed }, "phase", "pending_phase_shape_mismatch");
        AssertPendingInvalid(prepared with { Phase = SchedulePendingDeliveryPhase.ResultObserved }, "phase", "pending_phase_shape_mismatch");
        AssertPendingInvalid(prepared with { Phase = SchedulePendingDeliveryPhase.Unknown }, "phase", "unsupported_pending_phase");
        AssertPendingInvalid(prepared with { Phase = (SchedulePendingDeliveryPhase)99 }, "phase", "unsupported_pending_phase");
        AssertPendingInvalid(prepared with { CurrentEvidenceHash = null }, "prepared", "incomplete_preparation");
        AssertPendingInvalid(prepared with { RecurrenceProofHash = null }, "prepared", "incomplete_preparation");
        AssertPendingInvalid(prepared with { OverlapEvidenceHash = null }, "prepared", "incomplete_preparation");
        AssertPendingInvalid(prepared with { OverlapEvidenceHash = new string('A', 64) }, "overlapEvidenceHash", "invalid_hash");
        AssertPendingInvalid(prepared with { FinalizationPlan = null }, "prepared", "incomplete_preparation");
        AssertPendingInvalid(prepared with { Prepared = null }, "prepared", "incomplete_preparation");
        AssertPendingInvalid(claimed with { OverlapEvidenceHash = new string('8', 64) }, "prepared", "incomplete_preparation");
        AssertPendingInvalid(prepared with { Result = result }, "phase", "pending_phase_shape_mismatch");
        AssertPendingInvalid(ScheduleContractTestData.Pending() with { Result = result, Phase = SchedulePendingDeliveryPhase.ResultObserved }, "prepared", "prepared_delivery_required");
    }

    [Fact]
    public void Prepared_delivery_binds_exact_identity_occurrence_hash_and_time_trigger()
    {
        var prepared = ScheduleContractTestData.Prepared();
        Assert.True(ScheduleContractValidator.ValidatePreparedDelivery(prepared).IsValid);

        AssertPreparedInvalid(prepared with { SchemaVersion = 2 }, "schemaVersion", "unsupported_schema_version");
        AssertPreparedInvalid(prepared with { CanonicalEnvelopeHash = new string('0', 64) }, "canonicalEnvelopeHash", "envelope_hash_mismatch");
        AssertPreparedInvalid(prepared with { PreparedAtUtc = prepared.PreparedAtUtc.ToOffset(TimeSpan.FromHours(1)) }, "preparedAtUtc", "utc_required");
        AssertPreparedInvalid(prepared with { PreparedAtUtc = prepared.Envelope.Temporal.ReceivedAtUtc.AddTicks(-1) }, "preparedAtUtc", "prepared_before_received");
        AssertPreparedInvalid(ScheduleContractTestData.Prepared(kind: EmbodySense.Core.Common.Triggers.Models.TriggerKind.Webhook), "envelope.kind", "time_trigger_required");
        AssertPreparedInvalid(ScheduleContractTestData.Prepared(referencedPayload: true), "envelope.payload", "inline_payload_required");
        AssertPreparedInvalid(
            ScheduleContractTestData.Prepared(payload: TriggerDeliveryTestData.InlinePayload([0xff])),
            "envelope.payload",
            "invalid_utf8_payload");
        AssertPreparedInvalid(ScheduleContractTestData.Prepared(admitted: true), "envelope.visibleStatus", "preadmission_envelope_required");

        var wrongOccurrencePrepared = ScheduleContractTestData.Prepared(ScheduleContractTestData.OccurrenceAt(2));
        var pending = ScheduleContractTestData.Pending(prepared: wrongOccurrencePrepared);
        AssertPendingInvalid(pending, "prepared.envelope", "prepared_identity_mismatch");
        AssertPendingInvalid(pending, "prepared.envelope.temporal.createdAtUtc", "prepared_occurrence_mismatch");

        var mismatchedResult = ScheduleContractTestData.Result(new string('1', 64));
        AssertPendingInvalid(ScheduleContractTestData.Pending(prepared: prepared, result: mismatchedResult), "result.canonicalEnvelopeHash", "result_envelope_hash_mismatch");
    }

    [Fact]
    public void Finalization_plan_and_catch_up_episode_enforce_every_bound_and_limit_plus_one()
    {
        var maximum = Enumerable.Range(1, ScheduleContractLimits.MaxFinalizationEvidenceItems)
            .Select(index => ScheduleContractTestData.Disposition(index))
            .ToArray();
        var plan = new ScheduleFinalizationPlan(1, ScheduleContractTestData.OccurrenceAt(maximum.Length + 1L), null, null, maximum);
        Assert.True(ScheduleContractValidator.ValidateFinalizationPlan(plan).IsValid);

        var over = maximum.Append(ScheduleContractTestData.Disposition(maximum.Length + 1L)).ToArray();
        AssertPlanInvalid(plan with { NextOccurrence = ScheduleContractTestData.OccurrenceAt(over.Length + 1L), DispositionEvidence = over }, "dispositionEvidence", "evidence_limit_exceeded");
        AssertPlanInvalid(plan with { SchemaVersion = 2 }, "schemaVersion", "unsupported_schema_version");
        AssertPlanInvalid(plan with { DispositionEvidence = null! }, "dispositionEvidence", "required");

        var maximumEpisode = new ScheduleCatchUpEpisode(1, ScheduleContractLimits.MaxOccurrenceOrdinal, ScheduleContractLimits.MaxCatchUpOccurrences);
        Assert.True(ScheduleContractValidator.ValidateFinalizationPlan(new ScheduleFinalizationPlan(1, ScheduleContractTestData.OccurrenceAt(1), maximumEpisode, null, [])).IsValid);
        AssertPlanInvalid(new ScheduleFinalizationPlan(1, ScheduleContractTestData.OccurrenceAt(1), maximumEpisode with { RemainingAdmittedOccurrences = ScheduleContractLimits.MaxCatchUpOccurrences + 1 }, null, []), "catchUpEpisode.remainingAdmittedOccurrences", "catch_up_remaining_out_of_range");
        AssertPlanInvalid(new ScheduleFinalizationPlan(1, ScheduleContractTestData.OccurrenceAt(1), maximumEpisode with { LatestDueOrdinal = ScheduleContractLimits.MaxOccurrenceOrdinal + 1 }, null, []), "catchUpEpisode.latestDueOrdinal", "ordinal_out_of_range");
        AssertPlanInvalid(new ScheduleFinalizationPlan(1, null, maximumEpisode, null, []), "catchUpEpisode", "invalid_catch_up_episode");
    }

    [Fact]
    public void Result_terminal_and_deferred_contracts_reject_null_default_unknown_and_reordered_evidence()
    {
        Assert.False(ScheduleContractValidator.ValidateDeliveryResult(null).IsValid);
        var result = ScheduleContractTestData.Result(new string('7', 64));
        AssertResultInvalid(result with { SchemaVersion = 2 }, "schemaVersion", "unsupported_schema_version");
        AssertResultInvalid(result with { Kind = ScheduleDeliveryResultKind.Unknown }, "kind", "unsupported_delivery_result");
        AssertResultInvalid(result with { Kind = (ScheduleDeliveryResultKind)99 }, "kind", "unsupported_delivery_result");
        AssertResultInvalid(result with { ReasonCode = new string('r', ScheduleContractLimits.MaxReasonCodeCharacters + 1) }, "reasonCode", "invalid_reason_code");
        AssertResultInvalid(result with { CanonicalEnvelopeHash = new string('A', 64) }, "canonicalEnvelopeHash", "invalid_hash");
        AssertResultInvalid(result with { RecordedAtUtc = result.RecordedAtUtc.ToOffset(TimeSpan.FromHours(1)) }, "recordedAtUtc", "utc_required");

        Assert.False(ScheduleContractValidator.ValidateTerminalDeliveryEvidence(null).IsValid);
        var terminal = ScheduleContractTestData.Terminal(ScheduleContractTestData.Occurrence());
        AssertTerminalInvalid(terminal with { SchemaVersion = 2 }, "schemaVersion", "unsupported_schema_version");
        AssertTerminalInvalid(terminal with { CurrentEvidenceHash = new string('A', 64) }, "currentEvidenceHash", "invalid_hash");
        AssertTerminalInvalid(terminal with { RecurrenceProofHash = new string('A', 64) }, "recurrenceProofHash", "invalid_hash");
        AssertTerminalInvalid(terminal with { OverlapEvidenceHash = new string('A', 64) }, "overlapEvidenceHash", "invalid_hash");
        AssertTerminalInvalid(terminal with { Result = terminal.Result with { Kind = ScheduleDeliveryResultKind.Unavailable } }, "result.kind", "nonterminal_delivery_result");
        AssertTerminalInvalid(terminal with { FinalizedAtUtc = terminal.Result.RecordedAtUtc.AddTicks(-1) }, "finalizedAtUtc", "finalized_before_result");

        var overlapSkipped = ScheduleContractTestData.Disposition(
            1,
            disposition: ScheduleOccurrenceDisposition.OverlapSkipped);
        AssertDispositionInvalid(
            overlapSkipped with { DecisionEvidenceHash = null },
            "decisionEvidenceHash",
            "decision_evidence_required");
        AssertDispositionInvalid(
            overlapSkipped with { DecisionEvidenceHash = new string('A', 64) },
            "decisionEvidenceHash",
            "decision_evidence_required");
        AssertDispositionInvalid(
            ScheduleContractTestData.Disposition(1) with { DecisionEvidenceHash = new string('A', 64) },
            "decisionEvidenceHash",
            "invalid_hash");
        Assert.True(ScheduleContractValidator.ValidateDispositionEvidence(
            ScheduleContractTestData.Disposition(1) with { DecisionEvidenceHash = new string('7', 64) }).IsValid);

        var occurrence = ScheduleContractTestData.Occurrence();
        var deferred = ScheduleContractTestData.Deferred(occurrence);
        var deferredEvidence = ScheduleContractTestData.Disposition(
            occurrence.Ordinal,
            disposition: ScheduleOccurrenceDisposition.OverlapDeferred);
        Assert.True(ScheduleContractValidator.ValidateFinalizationPlan(new ScheduleFinalizationPlan(1, occurrence, null, deferred, [deferredEvidence])).IsValid);
        AssertPlanInvalid(new ScheduleFinalizationPlan(1, ScheduleContractTestData.OccurrenceAt(2), null, deferred, []), "deferredOccurrence.occurrence", "deferred_occurrence_mismatch");
        AssertPlanInvalid(new ScheduleFinalizationPlan(1, occurrence, null, deferred with { DeferredAtUtc = occurrence.ScheduledAtUtc.AddTicks(-1) }, []), "deferredOccurrence.deferredAtUtc", "deferred_before_occurrence");
    }

    [Fact]
    public void Validation_results_are_bounded_distinct_and_deterministically_sorted()
    {
        var malformed = Enumerable.Repeat<ScheduleOccurrenceDispositionEvidence>(null!, ScheduleContractLimits.MaxDispositionEvidenceItems + 1).ToArray();
        var state = ScheduleContractTestData.State() with { DispositionEvidence = malformed };
        var validation = ScheduleContractValidator.ValidateState(state);

        Assert.Equal(ScheduleContractLimits.MaxValidationErrors, validation.Errors.Count);
        Assert.Equal(validation.Errors.Distinct().Count(), validation.Errors.Count);
        Assert.Equal(
            validation.Errors.OrderBy(error => error.Path, StringComparer.Ordinal).ThenBy(error => error.Code, StringComparer.Ordinal),
            validation.Errors);

        AssertStateInvalid(ScheduleContractTestData.State() with { DispositionEvidence = null! }, "dispositionEvidence", "required");
        AssertStateInvalid(ScheduleContractTestData.State() with { TerminalDeliveryEvidence = null! }, "terminalDeliveryEvidence", "required");
        Assert.False(ScheduleContractValidator.ValidateState(null).IsValid);
        Assert.False(ScheduleContractValidator.ValidatePreparedDelivery(null).IsValid);
        Assert.False(ScheduleContractValidator.ValidateFinalizationPlan(null).IsValid);
        Assert.False(ScheduleContractValidator.ValidateDispositionEvidence(null).IsValid);
    }

    [Fact]
    public void Pending_and_state_enforce_all_revision_time_and_identity_bounds()
    {
        var pending = ScheduleContractTestData.Pending();
        AssertPendingInvalid(pending with { SchemaVersion = 2 }, "schemaVersion", "unsupported_schema_version");
        AssertPendingInvalid(pending with { Occurrence = pending.Occurrence with { Ordinal = 0 } }, "occurrence.ordinal", "ordinal_out_of_range");
        AssertPendingInvalid(pending with { ClaimId = null! }, "claimId", "invalid_claim_id");
        AssertPendingInvalid(pending with { ClaimedAtUtc = pending.ClaimedAtUtc.ToOffset(TimeSpan.FromHours(1)) }, "claimedAtUtc", "utc_required");
        AssertPendingInvalid(pending with { ClaimedAtUtc = pending.Occurrence.ScheduledAtUtc.AddTicks(-1) }, "claimedAtUtc", "claim_before_occurrence");

        AssertStateInvalid(ScheduleContractTestData.State() with { SchemaVersion = 2 }, "schemaVersion", "unsupported_schema_version");
        AssertStateInvalid(ScheduleContractTestData.State(definitionRevision: 0), "definitionRevision", "revision_out_of_range");
        AssertStateInvalid(ScheduleContractTestData.State(definitionRevision: ScheduleContractLimits.MaxRevision + 1), "definitionRevision", "revision_out_of_range");
        AssertStateInvalid(ScheduleContractTestData.State(stateRevision: 0), "stateRevision", "revision_out_of_range");
        AssertStateInvalid(ScheduleContractTestData.State(stateRevision: ScheduleContractLimits.MaxRevision + 1), "stateRevision", "revision_out_of_range");
        AssertStateInvalid(ScheduleContractTestData.State(definitionHash: new string('D', 64)), "definitionHash", "invalid_hash");
        AssertStateInvalid(ScheduleContractTestData.State(pending: pending, lastClockObservedAtUtc: pending.ClaimedAtUtc.AddTicks(-1)), "pendingDelivery.claimedAtUtc", "pending_claim_after_clock");
        AssertStateInvalid(ScheduleContractTestData.State(pending: pending, definitionHash: new string('1', 64)), "pendingDelivery.identity", "pending_identity_mismatch");
    }

    [Fact]
    public void Disposition_ranges_validate_exact_arithmetic_endpoints_singletons_and_utc_nullness()
    {
        var singleton = ScheduleContractTestData.Disposition(1);
        var range = ScheduleContractTestData.Disposition(2, 100);
        Assert.True(ScheduleContractValidator.ValidateDispositionEvidence(singleton).IsValid);
        Assert.True(ScheduleContractValidator.ValidateDispositionEvidence(range).IsValid);
        Assert.True(ScheduleContractValidator.ValidateDispositionEvidence(
            ScheduleContractTestData.Disposition(1, disposition: ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped)).IsValid);

        AssertDispositionInvalid(range with { Count = range.Count - 1 }, "count", "invalid_disposition_range");
        AssertDispositionInvalid(range with { LastOrdinal = range.FirstOrdinal - 1 }, "count", "invalid_disposition_range");
        AssertDispositionInvalid(singleton with { LastScheduledLocal = singleton.FirstScheduledLocal.AddTicks(1) }, "lastScheduledLocal", "invalid_disposition_range");
        AssertDispositionInvalid(range with { LastScheduledAtUtc = range.FirstScheduledAtUtc!.Value.AddTicks(-1) }, "lastScheduledAtUtc", "invalid_disposition_range");
        AssertDispositionInvalid(range with { Disposition = ScheduleOccurrenceDisposition.OverlapSkipped }, "count", "singleton_disposition_required");
        AssertDispositionInvalid(singleton with { FirstScheduledAtUtc = null }, "firstScheduledAtUtc", "invalid_disposition_utc");
        AssertDispositionInvalid(
            ScheduleContractTestData.Disposition(1, disposition: ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped) with { LastScheduledAtUtc = ScheduleContractTestData.FirstUtc },
            "firstScheduledAtUtc",
            "invalid_disposition_utc");
        AssertDispositionInvalid(singleton with { ReasonCode = new string('r', ScheduleContractLimits.MaxReasonCodeCharacters + 1) }, "reasonCode", "invalid_reason_code");
        AssertDispositionInvalid(singleton with { Disposition = ScheduleOccurrenceDisposition.Unknown }, "disposition", "unsupported_disposition");
    }

    [Fact]
    public void Evidence_collections_are_canonical_nonoverlapping_bounded_and_defensively_copied()
    {
        var first = ScheduleContractTestData.Disposition(1);
        var second = ScheduleContractTestData.Disposition(2);
        var caller = new[] { second, first };
        var state = ScheduleContractTestData.State(next: ScheduleContractTestData.OccurrenceAt(3), dispositions: caller);
        caller[0] = ScheduleContractTestData.Disposition(99);

        Assert.Equal([1L, 2L], state.DispositionEvidence.Select(item => item.FirstOrdinal));
        Assert.True(ScheduleContractValidator.ValidateState(state).IsValid, ScheduleContractTestData.Errors(ScheduleContractValidator.ValidateState(state)));

        var copy = ScheduleContractCopy.Copy(state)!;
        Assert.NotSame(state.DispositionEvidence, copy.DispositionEvidence);
        Assert.NotSame(state.DispositionEvidence[0], copy.DispositionEvidence[0]);
        Assert.NotSame(state.DispositionEvidence[0].TimeZone, copy.DispositionEvidence[0].TimeZone);

        var maximum = Enumerable.Range(1, ScheduleContractLimits.MaxDispositionEvidenceItems)
            .Select(index => ScheduleContractTestData.Disposition(index))
            .ToArray();
        Assert.True(ScheduleContractValidator.ValidateState(ScheduleContractTestData.State(next: ScheduleContractTestData.OccurrenceAt(maximum.Length + 1L), dispositions: maximum)).IsValid);
        var over = maximum.Append(ScheduleContractTestData.Disposition(maximum.Length + 1L)).ToArray();
        AssertStateInvalid(ScheduleContractTestData.State(next: ScheduleContractTestData.OccurrenceAt(over.Length + 1L), dispositions: over), "dispositionEvidence", "evidence_limit_exceeded");

        AssertStateInvalid(ScheduleContractTestData.State(next: ScheduleContractTestData.OccurrenceAt(5), dispositions: [ScheduleContractTestData.Disposition(1, 3), ScheduleContractTestData.Disposition(3, 4)]), "dispositionEvidence", "overlapping_evidence");
        AssertStateInvalid(ScheduleContractTestData.State(next: ScheduleContractTestData.OccurrenceAt(2), dispositions: [first, first]), "dispositionEvidence", "duplicate_evidence");
    }

    [Fact]
    public void Contract_copy_isolated_every_schedule_owned_nested_snapshot()
    {
        var definition = ScheduleContractTestData.Definition();
        var definitionCopy = ScheduleContractCopy.Copy(definition)!;
        Assert.Equal(definition, definitionCopy);
        Assert.NotSame(definition, definitionCopy);
        Assert.Same(definition.Target, definitionCopy.Target);
        Assert.NotSame(definition.AuthorityProfile, definitionCopy.AuthorityProfile);
        Assert.NotSame(definition.Recurrence, definitionCopy.Recurrence);
        Assert.NotSame(definition.TimeZone, definitionCopy.TimeZone);
        Assert.NotSame(definition.DaylightSaving, definitionCopy.DaylightSaving);
        Assert.NotSame(definition.Misfire, definitionCopy.Misfire);
        Assert.NotSame(definition.Payload, definitionCopy.Payload);

        var current = ScheduleContractTestData.Occurrence();
        var prepared = ScheduleContractTestData.Prepared(current);
        var successor = ScheduleContractTestData.OccurrenceAt(2);
        var plannedDeferred = ScheduleContractTestData.Deferred(successor);
        var plan = new ScheduleFinalizationPlan(1, successor, null, plannedDeferred, [ScheduleContractTestData.Disposition(2, disposition: ScheduleOccurrenceDisposition.OverlapDeferred)]);
        var pending = ScheduleContractTestData.Pending(current, prepared, finalizationPlan: plan);
        var state = ScheduleContractTestData.State(current, pending);
        var copy = ScheduleContractCopy.Copy(state)!;

        Assert.Equal(state.ScheduleId, copy.ScheduleId);
        Assert.Equal(state.DefinitionHash, copy.DefinitionHash);
        Assert.NotSame(state, copy);
        Assert.NotSame(state.NextOccurrence, copy.NextOccurrence);
        Assert.NotSame(state.NextOccurrence!.TimeZone, copy.NextOccurrence!.TimeZone);
        Assert.NotSame(state.PendingDelivery, copy.PendingDelivery);
        Assert.NotSame(state.PendingDelivery!.Identity, copy.PendingDelivery!.Identity);
        Assert.NotSame(state.PendingDelivery.Prepared, copy.PendingDelivery.Prepared);
        Assert.Same(state.PendingDelivery.Prepared!.Envelope, copy.PendingDelivery.Prepared!.Envelope);
        Assert.NotSame(state.PendingDelivery.FinalizationPlan, copy.PendingDelivery.FinalizationPlan);
        Assert.NotSame(state.PendingDelivery.FinalizationPlan!.DeferredOccurrence, copy.PendingDelivery.FinalizationPlan!.DeferredOccurrence);
        Assert.NotSame(state.PendingDelivery.FinalizationPlan.DispositionEvidence, copy.PendingDelivery.FinalizationPlan.DispositionEvidence);

        var terminalState = ScheduleContractTestData.State(
            next: ScheduleContractTestData.OccurrenceAt(2),
            terminal: [ScheduleContractTestData.Terminal(ScheduleContractTestData.OccurrenceAt(1))]);
        var terminalCopy = ScheduleContractCopy.Copy(terminalState)!;
        Assert.NotSame(terminalState.TerminalDeliveryEvidence, terminalCopy.TerminalDeliveryEvidence);
        Assert.NotSame(terminalState.TerminalDeliveryEvidence[0], terminalCopy.TerminalDeliveryEvidence[0]);
        Assert.NotSame(terminalState.TerminalDeliveryEvidence[0].Occurrence, terminalCopy.TerminalDeliveryEvidence[0].Occurrence);
        Assert.NotSame(terminalState.TerminalDeliveryEvidence[0].Result, terminalCopy.TerminalDeliveryEvidence[0].Result);

        Assert.Null(ScheduleContractCopy.Copy((ScheduleDefinition?)null));
        Assert.Null(ScheduleContractCopy.Copy((ScheduleState?)null));
        Assert.Null(ScheduleContractCopy.Copy((ScheduleOccurrence?)null));
        Assert.Null(ScheduleContractCopy.Copy((SchedulePendingDelivery?)null));
        Assert.Null(ScheduleContractCopy.Copy((ScheduleFinalizationPlan?)null));
        Assert.Null(ScheduleContractCopy.Copy((ScheduleDeferredOccurrence?)null));
    }

    [Fact]
    public void Catch_up_episode_budget_decrements_once_and_exhaustion_range_skips_the_frozen_remainder()
    {
        var current = ScheduleContractTestData.OccurrenceAt(1);
        var prepared = ScheduleContractTestData.Prepared(current);
        var successor = ScheduleContractTestData.OccurrenceAt(2);
        var currentEpisode = new ScheduleCatchUpEpisode(1, 5, 2);
        var successorEpisode = new ScheduleCatchUpEpisode(1, 5, 1);
        var plan = ScheduleContractTestData.FinalizationPlan(current, successor, successorEpisode);
        var pending = ScheduleContractTestData.Pending(current, prepared, finalizationPlan: plan);
        var state = ScheduleContractTestData.State(current, pending, catchUp: currentEpisode);
        Assert.True(ScheduleContractValidator.ValidateState(state).IsValid, ScheduleContractTestData.Errors(ScheduleContractValidator.ValidateState(state)));

        AssertStateInvalid(state with { PendingDelivery = pending with { FinalizationPlan = plan with { CatchUpEpisode = new ScheduleCatchUpEpisode(1, 5, 2) } } }, "pendingDelivery.finalizationPlan.catchUpEpisode", "invalid_catch_up_successor");
        AssertStateInvalid(state with { PendingDelivery = pending with { FinalizationPlan = plan with { CatchUpEpisode = new ScheduleCatchUpEpisode(1, 6, 1) } } }, "pendingDelivery.finalizationPlan.catchUpEpisode", "invalid_catch_up_successor");

        var lastAdmitted = ScheduleContractTestData.OccurrenceAt(2);
        var lastPrepared = ScheduleContractTestData.Prepared(lastAdmitted);
        var beyondEpisode = ScheduleContractTestData.OccurrenceAt(6);
        var skipped = ScheduleContractTestData.Disposition(3, 5);
        var exhaustedPlan = new ScheduleFinalizationPlan(1, beyondEpisode, null, null, [skipped]);
        var exhaustedPending = ScheduleContractTestData.Pending(lastAdmitted, lastPrepared, finalizationPlan: exhaustedPlan);
        var exhaustedState = ScheduleContractTestData.State(lastAdmitted, exhaustedPending, catchUp: new ScheduleCatchUpEpisode(1, 5, 1));
        Assert.True(ScheduleContractValidator.ValidateState(exhaustedState).IsValid, ScheduleContractTestData.Errors(ScheduleContractValidator.ValidateState(exhaustedState)));

        AssertStateInvalid(exhaustedState with { PendingDelivery = exhaustedPending with { FinalizationPlan = exhaustedPlan with { DispositionEvidence = [ScheduleContractTestData.Disposition(3, 4)] } } }, "pendingDelivery.finalizationPlan.dispositionEvidence", "catch_up_skip_range_incomplete");
        AssertStateInvalid(exhaustedState with { PendingDelivery = exhaustedPending with { FinalizationPlan = exhaustedPlan with { CatchUpEpisode = new ScheduleCatchUpEpisode(1, 5, 1) } } }, "pendingDelivery.finalizationPlan.catchUpEpisode", "catch_up_budget_renewed");

        var postEpisodeSuccessor = ScheduleContractTestData.OccurrenceAt(4);
        var invalidLocalAfterEpisode = ScheduleContractTestData.Disposition(
            3,
            disposition: ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped);
        var postEpisodePlan = new ScheduleFinalizationPlan(1, postEpisodeSuccessor, null, null, [invalidLocalAfterEpisode]);
        var postEpisodePending = ScheduleContractTestData.Pending(
            lastAdmitted,
            lastPrepared,
            finalizationPlan: postEpisodePlan);
        var postEpisodeState = ScheduleContractTestData.State(
            lastAdmitted,
            postEpisodePending,
            catchUp: new ScheduleCatchUpEpisode(1, 2, 1));
        var postEpisodeValidation = ScheduleContractValidator.ValidateState(postEpisodeState);
        Assert.True(postEpisodeValidation.IsValid, ScheduleContractTestData.Errors(postEpisodeValidation));

        AssertStateInvalid(
            postEpisodeState with
            {
                PendingDelivery = postEpisodePending with
                {
                    FinalizationPlan = postEpisodePlan with
                    {
                        DispositionEvidence = [ScheduleContractTestData.Disposition(3)],
                    },
                },
            },
            "pendingDelivery.finalizationPlan.dispositionEvidence",
            "catch_up_skip_range_exceeded");

        AssertStateInvalid(
            postEpisodeState with
            {
                PendingDelivery = postEpisodePending with
                {
                    FinalizationPlan = postEpisodePlan with
                    {
                        DispositionEvidence =
                        [
                            ScheduleContractTestData.Disposition(
                                3,
                                disposition: ScheduleOccurrenceDisposition.OverlapSkipped),
                        ],
                    },
                },
            },
            "pendingDelivery.finalizationPlan.dispositionEvidence",
            "catch_up_skip_range_exceeded");

        var deferredAfterEpisode = ScheduleContractTestData.OccurrenceAt(3);
        var overlapDeferred = ScheduleContractTestData.Disposition(
            3,
            disposition: ScheduleOccurrenceDisposition.OverlapDeferred);
        var overlapDeferredPlan = new ScheduleFinalizationPlan(
            1,
            deferredAfterEpisode,
            null,
            ScheduleContractTestData.Deferred(deferredAfterEpisode),
            [overlapDeferred]);
        var overlapDeferredPending = ScheduleContractTestData.Pending(
            lastAdmitted,
            lastPrepared,
            finalizationPlan: overlapDeferredPlan);
        AssertStateInvalid(
            ScheduleContractTestData.State(
                lastAdmitted,
                overlapDeferredPending,
                catchUp: new ScheduleCatchUpEpisode(1, 2, 1)),
            "pendingDelivery.finalizationPlan.dispositionEvidence",
            "catch_up_skip_range_exceeded");
    }

    [Fact]
    public void Explicit_overlap_deferral_is_bound_to_next_identity_and_reused_by_a_claim()
    {
        var occurrence = ScheduleContractTestData.Occurrence();
        var deferred = ScheduleContractTestData.Deferred(occurrence);
        var deferralEvidence = ScheduleContractTestData.Disposition(
            occurrence.Ordinal,
            disposition: ScheduleOccurrenceDisposition.OverlapDeferred);
        var state = ScheduleContractTestData.State(
            occurrence,
            dispositions: [deferralEvidence],
            deferred: deferred);
        Assert.True(ScheduleContractValidator.ValidateState(state).IsValid, ScheduleContractTestData.Errors(ScheduleContractValidator.ValidateState(state)));

        var claimed = ScheduleContractTestData.Pending(occurrence);
        Assert.True(ScheduleContractValidator.ValidateState(state with { PendingDelivery = claimed }).IsValid);

        AssertStateInvalid(state with { DeferredOccurrence = deferred with { Occurrence = ScheduleContractTestData.OccurrenceAt(2) } }, "deferredOccurrence.occurrence", "deferred_occurrence_mismatch");
        AssertStateInvalid(state with { DeferredOccurrence = deferred with { Identity = ScheduleContractTestData.Identity(ScheduleContractTestData.OccurrenceAt(2)) } }, "deferredOccurrence.identity", "deferred_identity_mismatch");
        AssertStateInvalid(state with { PendingDelivery = ScheduleContractTestData.Pending(ScheduleContractTestData.OccurrenceAt(2)) }, "pendingDelivery.occurrence", "pending_occurrence_mismatch");
    }

    [Fact]
    public void Terminal_delivery_history_is_conclusive_bounded_ordered_and_identity_pinned()
    {
        var first = ScheduleContractTestData.Terminal(ScheduleContractTestData.OccurrenceAt(1));
        var second = ScheduleContractTestData.Terminal(ScheduleContractTestData.OccurrenceAt(2), ScheduleDeliveryResultKind.Replayed);
        var state = ScheduleContractTestData.State(next: ScheduleContractTestData.OccurrenceAt(3), terminal: [second, first]);
        Assert.True(ScheduleContractValidator.ValidateState(state).IsValid, ScheduleContractTestData.Errors(ScheduleContractValidator.ValidateState(state)));
        Assert.Equal([1L, 2L], state.TerminalDeliveryEvidence.Select(item => item.Occurrence.Ordinal));

        AssertStateInvalid(state with { TerminalDeliveryEvidence = [first with { Result = first.Result with { Kind = ScheduleDeliveryResultKind.Ambiguous } }] }, "terminalDeliveryEvidence[0].result.kind", "nonterminal_delivery_result");
        AssertStateInvalid(state with { TerminalDeliveryEvidence = [first with { Identity = ScheduleContractTestData.Identity(ScheduleContractTestData.OccurrenceAt(2)) }] }, "terminalDeliveryEvidence[0].identity", "terminal_identity_mismatch");
        AssertStateInvalid(state with { PendingDelivery = ScheduleContractTestData.Pending(ScheduleContractTestData.OccurrenceAt(1)), NextOccurrence = ScheduleContractTestData.OccurrenceAt(1) }, "terminalDeliveryEvidence[0].identity", "pending_delivery_already_terminal");

        var maximum = Enumerable.Range(1, ScheduleContractLimits.MaxTerminalDeliveryEvidenceItems)
            .Select(index => ScheduleContractTestData.Terminal(ScheduleContractTestData.OccurrenceAt(index)))
            .ToArray();
        Assert.True(ScheduleContractValidator.ValidateState(ScheduleContractTestData.State(next: ScheduleContractTestData.OccurrenceAt(maximum.Length + 1L), terminal: maximum)).IsValid);
        var over = maximum.Append(ScheduleContractTestData.Terminal(ScheduleContractTestData.OccurrenceAt(maximum.Length + 1L))).ToArray();
        AssertStateInvalid(ScheduleContractTestData.State(next: ScheduleContractTestData.OccurrenceAt(over.Length + 1L), terminal: over), "terminalDeliveryEvidence", "evidence_limit_exceeded");
    }

    private static void AssertPendingValid(SchedulePendingDelivery pending)
    {
        var validation = ScheduleContractValidator.ValidatePendingDelivery(pending);
        Assert.True(validation.IsValid, ScheduleContractTestData.Errors(validation));
    }

    private static void AssertPendingInvalid(SchedulePendingDelivery pending, string path, string code)
    {
        var validation = ScheduleContractValidator.ValidatePendingDelivery(pending);
        Assert.Contains(validation.Errors, error => error.Path == path && error.Code == code);
    }

    private static void AssertPreparedInvalid(SchedulePreparedDelivery prepared, string path, string code)
    {
        var validation = ScheduleContractValidator.ValidatePreparedDelivery(prepared);
        Assert.Contains(validation.Errors, error => error.Path == path && error.Code == code);
    }

    private static void AssertDispositionInvalid(ScheduleOccurrenceDispositionEvidence evidence, string path, string code)
    {
        var validation = ScheduleContractValidator.ValidateDispositionEvidence(evidence);
        Assert.Contains(validation.Errors, error => error.Path == path && error.Code == code);
    }

    private static void AssertPlanInvalid(ScheduleFinalizationPlan plan, string path, string code)
    {
        var validation = ScheduleContractValidator.ValidateFinalizationPlan(plan);
        Assert.Contains(validation.Errors, error => error.Path == path && error.Code == code);
    }

    private static void AssertResultInvalid(ScheduleDeliveryResultEvidence result, string path, string code)
    {
        var validation = ScheduleContractValidator.ValidateDeliveryResult(result);
        Assert.Contains(validation.Errors, error => error.Path == path && error.Code == code);
    }

    private static void AssertTerminalInvalid(ScheduleTerminalDeliveryEvidence evidence, string path, string code)
    {
        var validation = ScheduleContractValidator.ValidateTerminalDeliveryEvidence(evidence);
        Assert.Contains(validation.Errors, error => error.Path == path && error.Code == code);
    }

    private static void AssertStateInvalid(ScheduleState state, string path, string code)
    {
        var validation = ScheduleContractValidator.ValidateState(state);
        Assert.Contains(validation.Errors, error => error.Path == path && error.Code == code);
    }
}
