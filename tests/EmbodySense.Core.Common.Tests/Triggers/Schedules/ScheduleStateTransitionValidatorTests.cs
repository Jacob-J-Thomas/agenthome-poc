using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Tests.Triggers.Schedules;

public sealed class ScheduleStateTransitionValidatorTests
{
    [Fact]
    public void Exact_claim_prepare_replay_observation_and_finalization_are_legal()
    {
        var definition = Definition(out var definitionHash);
        var occurrence = ScheduleContractTestData.Occurrence();
        var successor = ScheduleContractTestData.OccurrenceAt(2);
        var initial = State(definition, definitionHash, occurrence, revision: 1, clock: occurrence.ScheduledAtUtc);
        var claimedAtUtc = occurrence.ScheduledAtUtc.AddSeconds(1);
        var claimedPending = ScheduleContractTestData.Pending(
            occurrence,
            claimedAtUtc: claimedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var claimed = State(
            definition,
            definitionHash,
            occurrence,
            claimedPending,
            revision: 2,
            clock: claimedAtUtc);

        var preparedAtUtc = claimedAtUtc.AddSeconds(1);
        var preparedDelivery = ScheduleContractTestData.Prepared(
            occurrence,
            preparedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var plan = new ScheduleFinalizationPlan(1, successor, null, null, []);
        var preparedPending = ScheduleContractTestData.Pending(
            occurrence,
            preparedDelivery,
            finalizationPlan: plan,
            claimedAtUtc: claimedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var prepared = State(
            definition,
            definitionHash,
            occurrence,
            preparedPending,
            revision: 3,
            clock: preparedAtUtc);

        var observedAtUtc = preparedAtUtc.AddSeconds(1);
        var replay = ScheduleContractTestData.Result(
            preparedDelivery.CanonicalEnvelopeHash,
            ScheduleDeliveryResultKind.Replayed,
            observedAtUtc);
        var observedPending = preparedPending with
        {
            Phase = SchedulePendingDeliveryPhase.ResultObserved,
            Result = replay,
        };
        var observed = State(
            definition,
            definitionHash,
            occurrence,
            observedPending,
            revision: 4,
            clock: observedAtUtc);

        var finalizedAtUtc = observedAtUtc.AddSeconds(1);
        var terminal = Terminal(observedPending, finalizedAtUtc);
        var finalized = State(
            definition,
            definitionHash,
            successor,
            revision: 5,
            clock: finalizedAtUtc,
            terminal: [terminal]);

        AssertLegal(definition, initial, claimed);
        AssertLegal(definition, claimed, prepared);
        AssertLegal(definition, prepared, observed);
        AssertLegal(definition, observed, finalized);
    }

    [Fact]
    public void Exact_finalization_may_remove_one_ordered_terminal_evidence_at_the_bound()
    {
        var definition = Definition(out var definitionHash);
        var occurrence = ScheduleContractTestData.OccurrenceAt(ScheduleContractLimits.RetainedTerminalDeliveryEvidenceItems + 1L);
        var successor = ScheduleContractTestData.OccurrenceAt(occurrence.Ordinal + 1);
        var preparedAtUtc = occurrence.ScheduledAtUtc.AddSeconds(3);
        var prepared = ScheduleContractTestData.Prepared(
            occurrence,
            preparedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var result = ScheduleContractTestData.Result(prepared.CanonicalEnvelopeHash, recordedAtUtc: preparedAtUtc.AddSeconds(1)) with
        {
            ReasonCode = "q",
        };
        var plan = new ScheduleFinalizationPlan(1, successor, null, null, []);
        var pending = ScheduleContractTestData.Pending(
            occurrence,
            prepared,
            result,
            plan,
            occurrence.ScheduledAtUtc,
            definitionHash,
            definition.Revision,
            definition.ScheduleId);
        var finalizedAtUtc = result.RecordedAtUtc.AddSeconds(1);
        var priorTerminal = Enumerable.Range(1, ScheduleContractLimits.RetainedTerminalDeliveryEvidenceItems)
            .Select(index =>
            {
                var priorOccurrence = ScheduleContractTestData.OccurrenceAt(index);
                return new ScheduleTerminalDeliveryEvidence(
                    ScheduleTerminalDeliveryEvidence.CurrentSchemaVersion,
                    priorOccurrence,
                    ScheduleContractTestData.Identity(priorOccurrence, definitionHash, definition.Revision, definition.ScheduleId),
                    new string('f', ScheduleContractLimits.Sha256HexCharacters),
                    new string('9', ScheduleContractLimits.Sha256HexCharacters),
                    new string('8', ScheduleContractLimits.Sha256HexCharacters),
                    result,
                    result.RecordedAtUtc);
            })
            .ToArray();
        var current = State(
            definition,
            definitionHash,
            occurrence,
            pending,
            revision: 4,
            clock: result.RecordedAtUtc,
            terminal: priorTerminal);
        var appended = Terminal(pending, finalizedAtUtc);
        var rolled = State(
            definition,
            definitionHash,
            successor,
            revision: 5,
            clock: finalizedAtUtc,
            terminal: priorTerminal.Where((_, index) => index != 1).Append(appended).ToArray());

        Assert.Equal(appended, rolled.TerminalDeliveryEvidence[^1]);
        Assert.Equal(priorTerminal[0], rolled.TerminalDeliveryEvidence[0]);
        Assert.DoesNotContain(priorTerminal[1], rolled.TerminalDeliveryEvidence);
        var expected = current with
        {
            StateRevision = rolled.StateRevision,
            NextOccurrence = successor,
            LastClockObservedAtUtc = finalizedAtUtc,
            PendingDelivery = null,
            TerminalDeliveryEvidence = current.TerminalDeliveryEvidence.Where((_, index) => index != 1).Append(appended).ToArray(),
        };
        Assert.True(ScheduleContractHash.TryComputeState(expected, out var expectedHash, out var expectedValidation), ScheduleContractTestData.Errors(expectedValidation));
        Assert.True(ScheduleContractHash.TryComputeState(rolled, out var rolledHash, out var rolledValidation), ScheduleContractTestData.Errors(rolledValidation));
        Assert.Equal(expectedHash, rolledHash);
        AssertLegal(definition, current, rolled);
        AssertRejected(
            definition,
            current,
            rolled with
            {
                TerminalDeliveryEvidence = priorTerminal
                    .Where((_, index) => index != 1)
                    .Select(item => item == priorTerminal[2]
                        ? item with { Result = item.Result with { ReasonCode = "rewritten-result" } }
                        : item)
                    .Append(appended)
                    .ToArray(),
            },
            "terminal_evidence_rewritten");
    }

    [Fact]
    public void Evidence_removal_and_rewrite_are_rejected_even_when_each_snapshot_is_valid()
    {
        var definition = Definition(out var definitionHash);
        var successor = ScheduleContractTestData.OccurrenceAt(2);
        var disposition = Disposition(
            ScheduleContractTestData.Occurrence(),
            ScheduleOccurrenceDisposition.MisfireSkipped,
            ScheduleContractTestData.FirstUtc.AddSeconds(1));
        var current = State(
            definition,
            definitionHash,
            successor,
            revision: 1,
            clock: disposition.RecordedAtUtc,
            dispositions: [disposition]);

        AssertRejected(
            definition,
            current,
            State(
                definition,
                definitionHash,
                successor,
                revision: 2,
                clock: disposition.RecordedAtUtc.AddSeconds(1)),
            "disposition_evidence_rewritten");
        AssertRejected(
            definition,
            current,
            State(
                definition,
                definitionHash,
                successor,
                revision: 2,
                clock: disposition.RecordedAtUtc.AddSeconds(1),
                dispositions: [disposition with { ReasonCode = "rewritten-reason" }]),
            "disposition_evidence_rewritten");

        var terminal = TerminalFor(
            definition,
            definitionHash,
            ScheduleContractTestData.Occurrence(),
            ScheduleContractTestData.FirstUtc.AddSeconds(2));
        var terminalCurrent = State(
            definition,
            definitionHash,
            successor,
            revision: 1,
            clock: terminal.FinalizedAtUtc,
            terminal: [terminal]);
        AssertRejected(
            definition,
            terminalCurrent,
            State(
                definition,
                definitionHash,
                successor,
                revision: 2,
                clock: terminal.FinalizedAtUtc.AddSeconds(1)),
            "terminal_evidence_rewritten");
        AssertRejected(
            definition,
            terminalCurrent,
            State(
                definition,
                definitionHash,
                successor,
                revision: 2,
                clock: terminal.FinalizedAtUtc.AddSeconds(1),
                terminal:
                [
                    terminal with
                    {
                        Result = terminal.Result with { ReasonCode = "rewritten-result" },
                    },
                ]),
            "terminal_evidence_rewritten");
    }

    [Fact]
    public void Rewind_resurrection_and_premature_exhaustion_are_rejected()
    {
        var definition = Definition(out var definitionHash);
        var first = ScheduleContractTestData.Occurrence();
        var second = ScheduleContractTestData.OccurrenceAt(2);
        var clock = second.ScheduledAtUtc.AddSeconds(1);
        var current = State(definition, definitionHash, second, revision: 1, clock: clock);
        AssertRejected(
            definition,
            current,
            State(definition, definitionHash, first, revision: 2, clock: clock.AddSeconds(1)),
            "illegal_state_transition");

        var exhausted = State(definition, definitionHash, null, revision: 1, clock: clock);
        AssertRejected(
            definition,
            exhausted,
            State(definition, definitionHash, first, revision: 2, clock: clock.AddSeconds(1)),
            "illegal_state_transition");

        var claimedAtUtc = first.ScheduledAtUtc.AddSeconds(1);
        var pending = ScheduleContractTestData.Pending(
            first,
            claimedAtUtc: claimedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var claimed = State(definition, definitionHash, first, pending, 1, claimedAtUtc);
        var skippedAtUtc = claimedAtUtc.AddSeconds(1);
        var skipped = Disposition(first, ScheduleOccurrenceDisposition.MisfireSkipped, skippedAtUtc);
        AssertRejected(
            definition,
            claimed,
            State(
                definition,
                definitionHash,
                null,
                revision: 2,
                clock: skippedAtUtc,
                dispositions: [skipped]),
            "illegal_state_transition");
    }

    [Fact]
    public void Pending_phase_regression_replacement_and_ambiguous_retry_are_rejected()
    {
        var definition = Definition(out var definitionHash);
        var occurrence = ScheduleContractTestData.Occurrence();
        var claimedAtUtc = occurrence.ScheduledAtUtc.AddSeconds(1);
        var claimedPending = ScheduleContractTestData.Pending(
            occurrence,
            claimedAtUtc: claimedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var claimed = State(definition, definitionHash, occurrence, claimedPending, 1, claimedAtUtc);
        Assert.True(ScheduleClaimId.TryParse("claim-replaced", out var replacementClaim));
        var replacedClaim = State(
            definition,
            definitionHash,
            occurrence,
            claimedPending with { ClaimId = replacementClaim! },
            2,
            claimedAtUtc.AddSeconds(1));
        AssertRejected(definition, claimed, replacedClaim, "illegal_state_transition");

        var preparedAtUtc = claimedAtUtc.AddSeconds(1);
        var preparedDelivery = ScheduleContractTestData.Prepared(
            occurrence,
            preparedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var preparedPending = ScheduleContractTestData.Pending(
            occurrence,
            preparedDelivery,
            claimedAtUtc: claimedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var prepared = State(definition, definitionHash, occurrence, preparedPending, 2, preparedAtUtc);
        var regressed = State(
            definition,
            definitionHash,
            occurrence,
            claimedPending,
            3,
            preparedAtUtc.AddSeconds(1));
        AssertRejected(definition, prepared, regressed, "illegal_state_transition");

        var ambiguousAtUtc = preparedAtUtc.AddSeconds(1);
        var ambiguousPending = preparedPending with
        {
            Phase = SchedulePendingDeliveryPhase.ResultObserved,
            Result = ScheduleContractTestData.Result(
                preparedDelivery.CanonicalEnvelopeHash,
                ScheduleDeliveryResultKind.Ambiguous,
                ambiguousAtUtc),
        };
        var ambiguous = State(definition, definitionHash, occurrence, ambiguousPending, 3, ambiguousAtUtc);
        var cleared = State(
            definition,
            definitionHash,
            occurrence,
            preparedPending,
            4,
            ambiguousAtUtc.AddSeconds(1));
        AssertRejected(definition, ambiguous, cleared, "illegal_state_transition");

        var replayAtUtc = ambiguousAtUtc.AddSeconds(1);
        var retried = State(
            definition,
            definitionHash,
            occurrence,
            ambiguousPending with
            {
                Result = ScheduleContractTestData.Result(
                    preparedDelivery.CanonicalEnvelopeHash,
                    ScheduleDeliveryResultKind.Replayed,
                    replayAtUtc),
            },
            4,
            replayAtUtc);
        AssertRejected(definition, ambiguous, retried, "illegal_state_transition");
    }

    [Fact]
    public void Immutable_coordinates_clock_and_enable_control_cannot_be_smuggled_with_semantic_changes()
    {
        var definition = Definition(out var definitionHash);
        var occurrence = ScheduleContractTestData.Occurrence();
        var clock = occurrence.ScheduledAtUtc.AddSeconds(10);
        var current = State(definition, definitionHash, occurrence, revision: 1, clock: clock, enabled: false);

        AssertRejected(
            definition,
            current,
            State(definition, definitionHash, occurrence, revision: 2, clock: clock.AddTicks(-1), enabled: false),
            "clock_regressed");
        AssertRejected(
            definition,
            current,
            State(definition, definitionHash, occurrence, revision: 2, clock: clock.AddSeconds(1), enabled: true),
            "illegal_state_transition");

        var claimedAtUtc = clock.AddSeconds(1);
        var disabledClaim = State(
            definition,
            definitionHash,
            occurrence,
            ScheduleContractTestData.Pending(
                occurrence,
                claimedAtUtc: claimedAtUtc,
                definitionHash: definitionHash,
                scheduleId: definition.ScheduleId),
            revision: 2,
            clock: claimedAtUtc,
            enabled: false);
        AssertRejected(definition, current, disabledClaim, "illegal_state_transition");

        var changedRevision = current with
        {
            DefinitionRevision = 2,
            StateRevision = 2,
        };
        Assert.False(ScheduleStateTransitionValidator.Validate(definition, current, changedRevision).IsValid);
    }

    [Fact]
    public void Existing_catch_up_episode_cannot_be_replaced_during_preparation()
    {
        var definition = Definition(out var definitionHash);
        var occurrence = ScheduleContractTestData.Occurrence();
        var claimedAtUtc = occurrence.ScheduledAtUtc.AddSeconds(1);
        var claimedPending = ScheduleContractTestData.Pending(
            occurrence,
            claimedAtUtc: claimedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var currentEpisode = new ScheduleCatchUpEpisode(1, 3, 2);
        var claimed = State(
            definition,
            definitionHash,
            occurrence,
            claimedPending,
            revision: 1,
            clock: claimedAtUtc,
            catchUp: currentEpisode);
        var preparedAtUtc = claimedAtUtc.AddSeconds(1);
        var replacedEpisode = new ScheduleCatchUpEpisode(1, 4, 2);
        var plan = new ScheduleFinalizationPlan(
            1,
            ScheduleContractTestData.OccurrenceAt(2),
            new ScheduleCatchUpEpisode(1, 4, 1),
            null,
            []);
        var preparedPending = ScheduleContractTestData.Pending(
            occurrence,
            ScheduleContractTestData.Prepared(
                occurrence,
                preparedAtUtc,
                definitionHash: definitionHash,
                scheduleId: definition.ScheduleId),
            finalizationPlan: plan,
            claimedAtUtc: claimedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var replacement = State(
            definition,
            definitionHash,
            occurrence,
            preparedPending,
            revision: 2,
            clock: preparedAtUtc,
            catchUp: replacedEpisode);

        AssertRejected(definition, claimed, replacement, "illegal_state_transition");
    }

    [Fact]
    public void Direct_skip_cannot_forge_a_recurrence_successor()
    {
        var definition = Definition(
            out var definitionHash,
            ScheduleRecurrenceKind.FixedInterval,
            60);
        var occurrence = ScheduleContractTestData.Occurrence();
        var claimedAtUtc = occurrence.ScheduledAtUtc.AddSeconds(1);
        var pending = ScheduleContractTestData.Pending(
            occurrence,
            claimedAtUtc: claimedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var claimed = State(definition, definitionHash, occurrence, pending, 1, claimedAtUtc);
        var skippedAtUtc = claimedAtUtc.AddSeconds(1);
        var forgedSuccessor = ScheduleContractTestData.Occurrence(
            2,
            ScheduleContractTestData.FirstLocal.AddMinutes(2),
            ScheduleContractTestData.FirstUtc.AddMinutes(2),
            definition.TimeZone);
        var skipped = State(
            definition,
            definitionHash,
            forgedSuccessor,
            revision: 2,
            clock: skippedAtUtc,
            dispositions:
            [
                Disposition(
                    occurrence,
                    ScheduleOccurrenceDisposition.MisfireSkipped,
                    skippedAtUtc),
            ]);

        AssertRejected(definition, claimed, skipped, "illegal_state_transition");
    }

    [Theory]
    [InlineData(ScheduleRecurrenceKind.Daily, 1)]
    [InlineData(ScheduleRecurrenceKind.Weekly, 7)]
    public void Direct_calendar_skip_cannot_rewind_the_successor_utc_instant(
        ScheduleRecurrenceKind recurrence,
        int periodDays)
    {
        var definition = Definition(out var definitionHash, recurrence);
        var occurrence = ScheduleContractTestData.Occurrence();
        var claimedAtUtc = occurrence.ScheduledAtUtc.AddSeconds(1);
        var pending = ScheduleContractTestData.Pending(
            occurrence,
            claimedAtUtc: claimedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var claimed = State(definition, definitionHash, occurrence, pending, 1, claimedAtUtc);
        var skippedAtUtc = claimedAtUtc.AddSeconds(1);
        var forgedSuccessor = ScheduleContractTestData.Occurrence(
            2,
            occurrence.ScheduledLocal.AddDays(periodDays),
            occurrence.ScheduledAtUtc.AddTicks(-1),
            definition.TimeZone);
        var skipped = State(
            definition,
            definitionHash,
            forgedSuccessor,
            revision: 2,
            clock: skippedAtUtc,
            dispositions:
            [
                Disposition(
                    occurrence,
                    ScheduleOccurrenceDisposition.MisfireSkipped,
                    skippedAtUtc),
            ]);

        AssertRejected(definition, claimed, skipped, "illegal_state_transition");
    }

    [Fact]
    public void Direct_skip_cannot_renew_or_replace_an_active_catch_up_budget()
    {
        var definition = Definition(out var definitionHash);
        var occurrence = ScheduleContractTestData.Occurrence();
        var claimedAtUtc = occurrence.ScheduledAtUtc.AddSeconds(1);
        var pending = ScheduleContractTestData.Pending(
            occurrence,
            claimedAtUtc: claimedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var episode = new ScheduleCatchUpEpisode(1, 3, 2);
        var claimed = State(
            definition,
            definitionHash,
            occurrence,
            pending,
            1,
            claimedAtUtc,
            catchUp: episode);
        var skippedAtUtc = claimedAtUtc.AddSeconds(1);
        var evidence = Disposition(
            occurrence,
            ScheduleOccurrenceDisposition.MisfireSkipped,
            skippedAtUtc);

        var renewed = State(
            definition,
            definitionHash,
            ScheduleContractTestData.OccurrenceAt(2),
            revision: 2,
            clock: skippedAtUtc,
            catchUp: episode,
            dispositions: [evidence]);
        var replaced = renewed with
        {
            CatchUpEpisode = new ScheduleCatchUpEpisode(1, 4, 1),
        };

        AssertRejected(definition, claimed, renewed, "illegal_state_transition");
        AssertRejected(definition, claimed, replaced, "illegal_state_transition");
    }

    [Fact]
    public void Canonical_insertion_is_not_misclassified_as_evidence_rewrite()
    {
        var definition = Definition(out var definitionHash);
        var third = ScheduleContractTestData.OccurrenceAt(3);
        var secondEvidence = Disposition(
            ScheduleContractTestData.OccurrenceAt(2),
            ScheduleOccurrenceDisposition.MisfireSkipped,
            ScheduleContractTestData.FirstUtc.AddDays(1).AddSeconds(1));
        var current = State(
            definition,
            definitionHash,
            third,
            revision: 1,
            clock: secondEvidence.RecordedAtUtc,
            dispositions: [secondEvidence]);
        var firstEvidence = Disposition(
            ScheduleContractTestData.Occurrence(),
            ScheduleOccurrenceDisposition.MisfireSkipped,
            secondEvidence.RecordedAtUtc);
        var replacement = State(
            definition,
            definitionHash,
            third,
            revision: 2,
            clock: secondEvidence.RecordedAtUtc.AddSeconds(1),
            dispositions: [secondEvidence, firstEvidence]);

        var validation = ScheduleStateTransitionValidator.Validate(definition, current, replacement);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Code == "illegal_state_transition");
        Assert.DoesNotContain(validation.Errors, error => error.Code == "disposition_evidence_rewritten");
    }

    [Fact]
    public void Final_skip_retains_prior_deferral_evidence_and_clears_only_the_active_pointer()
    {
        var definition = Definition(out var definitionHash);
        var occurrence = ScheduleContractTestData.Occurrence();
        var successor = ScheduleContractTestData.OccurrenceAt(2);
        var deferredAtUtc = occurrence.ScheduledAtUtc.AddSeconds(1);
        var deferralEvidence = Disposition(
            occurrence,
            ScheduleOccurrenceDisposition.OverlapDeferred,
            deferredAtUtc);
        var deferred = new ScheduleDeferredOccurrence(
            ScheduleDeferredOccurrence.CurrentSchemaVersion,
            occurrence,
            ScheduleContractTestData.Identity(
                occurrence,
                definitionHash,
                definition.Revision,
                definition.ScheduleId),
            deferredAtUtc);
        var claimedAtUtc = deferredAtUtc.AddSeconds(1);
        var pending = ScheduleContractTestData.Pending(
            occurrence,
            claimedAtUtc: claimedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var claimed = State(
            definition,
            definitionHash,
            occurrence,
            pending,
            revision: 1,
            clock: claimedAtUtc,
            deferred: deferred,
            dispositions: [deferralEvidence]);
        var skippedAtUtc = claimedAtUtc.AddSeconds(1);
        var finalSkip = Disposition(
            occurrence,
            ScheduleOccurrenceDisposition.MisfireSkipped,
            skippedAtUtc);
        var skipped = State(
            definition,
            definitionHash,
            successor,
            revision: 2,
            clock: skippedAtUtc,
            dispositions: [deferralEvidence, finalSkip]);

        AssertLegal(definition, claimed, skipped);
        Assert.Equal(2, skipped.DispositionEvidence.Count);
        Assert.Contains(deferralEvidence, skipped.DispositionEvidence);
        Assert.Contains(finalSkip, skipped.DispositionEvidence);
        Assert.Null(skipped.DeferredOccurrence);
    }

    [Fact]
    public void Maximum_revision_and_occurrence_bounds_do_not_overflow()
    {
        var definition = Definition(
            out var definitionHash,
            ScheduleRecurrenceKind.FixedInterval,
            ScheduleContractLimits.MaxFixedIntervalSeconds);
        var maximum = ScheduleContractTestData.Occurrence(
            ScheduleContractLimits.MaxOccurrenceOrdinal,
            ScheduleContractTestData.FirstLocal,
            ScheduleContractTestData.FirstUtc,
            definition.TimeZone);
        var claimedAtUtc = maximum.ScheduledAtUtc.AddSeconds(1);
        var pending = ScheduleContractTestData.Pending(
            maximum,
            claimedAtUtc: claimedAtUtc,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        var claimed = State(definition, definitionHash, maximum, pending, 1, claimedAtUtc);
        var skippedAtUtc = claimedAtUtc.AddSeconds(1);
        var exhausted = State(
            definition,
            definitionHash,
            null,
            revision: 2,
            clock: skippedAtUtc,
            dispositions:
            [
                Disposition(
                    maximum,
                    ScheduleOccurrenceDisposition.MisfireSkipped,
                    skippedAtUtc),
            ]);

        AssertLegal(definition, claimed, exhausted);

        var revisionBound = State(
            definition,
            definitionHash,
            maximum,
            revision: ScheduleContractLimits.MaxRevision,
            clock: claimedAtUtc);
        var validation = ScheduleStateTransitionValidator.Validate(
            definition,
            revisionBound,
            revisionBound with { LastClockObservedAtUtc = skippedAtUtc });
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Code == "invalid_successor_revision");
    }

    private static ScheduleDefinition Definition(
        out string definitionHash,
        ScheduleRecurrenceKind recurrence = ScheduleRecurrenceKind.Daily,
        long? fixedIntervalSeconds = null)
    {
        var definition = ScheduleContractTestData.Definition(
            recurrenceKind: recurrence,
            fixedIntervalSeconds: fixedIntervalSeconds);
        Assert.True(
            ScheduleContractHash.TryComputeDefinition(definition, out var hash, out var validation),
            ScheduleContractTestData.Errors(validation));
        definitionHash = hash!;
        return definition;
    }

    private static ScheduleState State(
        ScheduleDefinition definition,
        string definitionHash,
        ScheduleOccurrence? next,
        SchedulePendingDelivery? pending = null,
        long revision = 1,
        DateTimeOffset? clock = null,
        bool enabled = true,
        ScheduleCatchUpEpisode? catchUp = null,
        ScheduleDeferredOccurrence? deferred = null,
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence>? dispositions = null,
        IReadOnlyList<ScheduleTerminalDeliveryEvidence>? terminal = null)
        => new(
            ScheduleState.CurrentSchemaVersion,
            definition.ScheduleId,
            definition.Revision,
            definitionHash,
            revision,
            enabled,
            next,
            catchUp,
            deferred,
            clock,
            pending,
            dispositions ?? [],
            terminal ?? []);

    private static ScheduleOccurrenceDispositionEvidence Disposition(
        ScheduleOccurrence occurrence,
        ScheduleOccurrenceDisposition disposition,
        DateTimeOffset recordedAtUtc)
        => new(
            ScheduleOccurrenceDispositionEvidence.CurrentSchemaVersion,
            occurrence.Ordinal,
            occurrence.Ordinal,
            1,
            occurrence.ScheduledLocal,
            occurrence.ScheduledLocal,
            occurrence.ScheduledAtUtc,
            occurrence.ScheduledAtUtc,
            occurrence.TimeZone,
            disposition,
            disposition is ScheduleOccurrenceDisposition.OverlapSkipped or ScheduleOccurrenceDisposition.OverlapDeferred
                ? new string('8', ScheduleContractLimits.Sha256HexCharacters)
                : null,
            disposition == ScheduleOccurrenceDisposition.OverlapDeferred
                ? "overlap-policy-defer"
                : "misfire-policy-skip",
            recordedAtUtc);

    private static ScheduleTerminalDeliveryEvidence Terminal(
        SchedulePendingDelivery pending,
        DateTimeOffset finalizedAtUtc)
        => new(
            ScheduleTerminalDeliveryEvidence.CurrentSchemaVersion,
            pending.Occurrence,
            pending.Identity,
            pending.CurrentEvidenceHash!,
            pending.RecurrenceProofHash!,
            pending.OverlapEvidenceHash!,
            pending.Result!,
            finalizedAtUtc);

    private static ScheduleTerminalDeliveryEvidence TerminalFor(
        ScheduleDefinition definition,
        string definitionHash,
        ScheduleOccurrence occurrence,
        DateTimeOffset finalizedAtUtc)
    {
        var result = ScheduleContractTestData.Result(
            new string('7', ScheduleContractLimits.Sha256HexCharacters),
            recordedAtUtc: finalizedAtUtc.AddTicks(-1));
        return new ScheduleTerminalDeliveryEvidence(
            ScheduleTerminalDeliveryEvidence.CurrentSchemaVersion,
            occurrence,
            ScheduleContractTestData.Identity(
                occurrence,
                definitionHash,
                definition.Revision,
                definition.ScheduleId),
            new string('f', ScheduleContractLimits.Sha256HexCharacters),
            new string('9', ScheduleContractLimits.Sha256HexCharacters),
            new string('8', ScheduleContractLimits.Sha256HexCharacters),
            result,
            finalizedAtUtc);
    }

    private static void AssertLegal(
        ScheduleDefinition definition,
        ScheduleState current,
        ScheduleState next)
    {
        var validation = ScheduleStateTransitionValidator.Validate(definition, current, next);
        Assert.True(validation.IsValid, ScheduleContractTestData.Errors(validation));
    }

    private static void AssertRejected(
        ScheduleDefinition definition,
        ScheduleState current,
        ScheduleState next,
        string expectedCode)
    {
        var currentValidation = ScheduleContractValidator.ValidateDefinitionStateComposition(definition, current);
        var nextValidation = ScheduleContractValidator.ValidateDefinitionStateComposition(definition, next);
        Assert.True(currentValidation.IsValid, ScheduleContractTestData.Errors(currentValidation));
        Assert.True(nextValidation.IsValid, ScheduleContractTestData.Errors(nextValidation));
        var validation = ScheduleStateTransitionValidator.Validate(definition, current, next);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Code == expectedCode);
    }
}
