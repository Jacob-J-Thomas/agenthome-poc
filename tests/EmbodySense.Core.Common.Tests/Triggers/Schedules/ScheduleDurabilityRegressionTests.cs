using System.Collections;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Tests.Triggers.Schedules;

public sealed class ScheduleDurabilityRegressionTests
{
    [Fact]
    public void Definition_state_composition_covers_idle_claimed_and_one_way_disablement()
    {
        var definition = ScheduleContractTestData.Definition();
        var definitionHash = DefinitionHash(definition);
        var occurrence = ScheduleContractTestData.Occurrence();
        var idle = ScheduleContractTestData.State(
            occurrence,
            definitionRevision: definition.Revision,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        AssertDefinitionStateValid(definition, idle);

        var claimed = ScheduleContractTestData.Pending(
            occurrence,
            definitionHash: definitionHash,
            definitionRevision: definition.Revision,
            scheduleId: definition.ScheduleId);
        var claimedState = ScheduleContractTestData.State(
            occurrence,
            claimed,
            definitionRevision: definition.Revision,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        AssertDefinitionStateValid(definition, claimedState);
        AssertDefinitionStateValid(definition, claimedState with { Enabled = false });

        var disabledDefinition = definition with { Enabled = false };
        var disabledHash = DefinitionHash(disabledDefinition);
        var incorrectlyEnabled = ScheduleContractTestData.State(
            occurrence,
            definitionRevision: disabledDefinition.Revision,
            definitionHash: disabledHash,
            scheduleId: disabledDefinition.ScheduleId);
        AssertDefinitionStateInvalid(
            disabledDefinition,
            incorrectlyEnabled,
            "state.enabled",
            "definition_disabled");
        AssertDefinitionStateValid(disabledDefinition, incorrectlyEnabled with { Enabled = false });

        var offZoneOccurrence = occurrence with
        {
            TimeZone = ScheduleContractTestData.TimeZone("America/New_York"),
        };
        var offZoneIdle = ScheduleContractTestData.State(
            offZoneOccurrence,
            definitionRevision: definition.Revision,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        AssertDefinitionStateInvalid(
            definition,
            offZoneIdle,
            "state.nextOccurrence.timeZone",
            "definition_time_zone_mismatch");

        var offAnchorOccurrence = ScheduleContractTestData.Occurrence(
            2,
            ScheduleContractTestData.FirstLocal,
            ScheduleContractTestData.FirstUtc.AddDays(1));
        var offAnchorIdle = ScheduleContractTestData.State(
            offAnchorOccurrence,
            definitionRevision: definition.Revision,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
        AssertDefinitionStateInvalid(
            definition,
            offAnchorIdle,
            "state.nextOccurrence.scheduledLocal",
            "recurrence_anchor_mismatch");
    }

    [Fact]
    public void Prepared_composition_binds_every_definition_and_state_coordinate()
    {
        var definition = ScheduleContractTestData.Definition();
        var definitionHash = DefinitionHash(definition);
        var occurrence = ScheduleContractTestData.Occurrence();
        AssertCompositionValid(definition, PreparedState(definition, definitionHash, occurrence));

        AssertCompositionInvalid(
            definition,
            PreparedState(definition, definitionHash, occurrence, target: TriggerDeliveryTestData.GovernedLoop(graphId: "another-loop")),
            "state.pendingDelivery.prepared.envelope.loop",
            "target_mismatch");
        AssertCompositionInvalid(
            definition,
            PreparedState(
                definition,
                definitionHash,
                occurrence,
                adapter: TriggerDeliveryTestData.Adapter("org.embodysense/triggers/clock", implementation: "triggers/clock")),
            "state.pendingDelivery.prepared.envelope.adapter",
            "adapter_mismatch");
        AssertCompositionInvalid(
            definition,
            PreparedState(
                definition,
                definitionHash,
                occurrence,
                actorContext: TriggerDeliveryTestData.ActorContext(surface: "another-surface")),
            "state.pendingDelivery.prepared.envelope.actorContext",
            "actor_context_mismatch");
        AssertCompositionInvalid(
            definition,
            PreparedState(
                definition,
                definitionHash,
                occurrence,
                authority: TriggerDeliveryTestData.Authority(profileIdText: "another-profile")),
            "state.pendingDelivery.prepared.envelope.authority.profile",
            "authority_profile_mismatch");
        AssertCompositionInvalid(
            definition,
            PreparedState(
                definition,
                definitionHash,
                occurrence,
                payload: TriggerDeliveryTestData.InlinePayload([5, 6, 7])),
            "state.pendingDelivery.prepared.envelope.payload",
            "payload_mismatch");

        var wrongHash = new string('1', ScheduleContractLimits.Sha256HexCharacters);
        var wrongHashPrepared = ScheduleContractTestData.Prepared(occurrence, definitionHash: wrongHash);
        var wrongHashPending = ScheduleContractTestData.Pending(occurrence, wrongHashPrepared, definitionHash: wrongHash);
        AssertCompositionInvalid(
            definition,
            ScheduleContractTestData.State(occurrence, wrongHashPending, definitionHash: wrongHash),
            "state.definitionHash",
            "definition_state_mismatch");

        var revisionPrepared = ScheduleContractTestData.Prepared(occurrence, definitionHash: definitionHash, definitionRevision: 2);
        var revisionPending = ScheduleContractTestData.Pending(
            occurrence,
            revisionPrepared,
            definitionHash: definitionHash,
            definitionRevision: 2);
        AssertCompositionInvalid(
            definition,
            ScheduleContractTestData.State(occurrence, revisionPending, definitionRevision: 2, definitionHash: definitionHash),
            "state.definitionHash",
            "definition_state_mismatch");

        Assert.True(ScheduleId.TryParse("another-schedule", out var otherSchedule));
        var schedulePrepared = ScheduleContractTestData.Prepared(
            occurrence,
            definitionHash: definitionHash,
            scheduleId: otherSchedule);
        var schedulePending = ScheduleContractTestData.Pending(
            occurrence,
            schedulePrepared,
            definitionHash: definitionHash,
            scheduleId: otherSchedule);
        AssertCompositionInvalid(
            definition,
            ScheduleContractTestData.State(
                occurrence,
                schedulePending,
                definitionHash: definitionHash,
                scheduleId: otherSchedule),
            "state.definitionHash",
            "definition_state_mismatch");
    }

    [Fact]
    public void Prepared_composition_closes_unrepresented_conversation_temporal_and_redelivery_behavior()
    {
        var definition = ScheduleContractTestData.Definition();
        var definitionHash = DefinitionHash(definition);
        var occurrence = ScheduleContractTestData.Occurrence();
        var conversation = new CustomLoopConversationReference(
            "conversation-1",
            "version-1",
            occurrence.ScheduledAtUtc.AddSeconds(1));
        var publicationState = PreparedState(
            definition,
            definitionHash,
            occurrence,
            publicationRequested: true,
            conversation: conversation);
        AssertCompositionInvalid(
            definition,
            publicationState,
            "state.pendingDelivery.prepared.envelope.publicationRequested",
            "publication_not_supported");
        AssertCompositionInvalid(
            definition,
            publicationState,
            "state.pendingDelivery.prepared.envelope.invokingConversation",
            "invoking_conversation_not_supported");

        var temporal = TriggerDeliveryTestData.Temporal(
            createdAtUtc: occurrence.ScheduledAtUtc,
            observedAtUtc: occurrence.ScheduledAtUtc.AddSeconds(1),
            receivedAtUtc: occurrence.ScheduledAtUtc.AddSeconds(2),
            notBeforeUtc: occurrence.ScheduledAtUtc.AddSeconds(3),
            deadlineUtc: occurrence.ScheduledAtUtc.AddSeconds(4),
            expiresAtUtc: occurrence.ScheduledAtUtc.AddSeconds(5));
        var gatedState = PreparedState(definition, definitionHash, occurrence, temporal: temporal);
        AssertCompositionInvalid(definition, gatedState, "state.pendingDelivery.prepared.envelope.temporal.notBeforeUtc", "temporal_gate_not_supported");
        AssertCompositionInvalid(definition, gatedState, "state.pendingDelivery.prepared.envelope.temporal.deadlineUtc", "temporal_gate_not_supported");
        AssertCompositionInvalid(definition, gatedState, "state.pendingDelivery.prepared.envelope.temporal.expiresAtUtc", "temporal_gate_not_supported");

        var identity = ScheduleContractTestData.Identity(occurrence, definitionHash);
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(2, 2, identity.DeliveryId, out var redelivery, out _));
        AssertCompositionInvalid(
            definition,
            PreparedState(definition, definitionHash, occurrence, redelivery: redelivery),
            "state.pendingDelivery.prepared.envelope.redelivery",
            "initial_redelivery_required");
    }

    [Fact]
    public void Definition_aware_recurrence_accepts_fixed_interval_dst_fold_and_checks_each_closed_kind()
    {
        var foldCurrent = ScheduleContractTestData.Occurrence(
            scheduledLocal: new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified),
            scheduledAtUtc: new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero));
        var foldNext = ScheduleContractTestData.Occurrence(
            2,
            new DateTime(2026, 11, 1, 1, 0, 0, DateTimeKind.Unspecified),
            foldCurrent.ScheduledAtUtc.AddHours(1));
        var fixedDefinition = ScheduleContractTestData.Definition(
            recurrenceKind: ScheduleRecurrenceKind.FixedInterval,
            fixedIntervalSeconds: 3600) with
        {
            Recurrence = new ScheduleRecurrenceRule(
                ScheduleRecurrenceKind.FixedInterval,
                foldCurrent.ScheduledLocal,
                3600),
        };
        var fixedHash = DefinitionHash(fixedDefinition);
        var fixedPlan = new ScheduleFinalizationPlan(1, foldNext, null, null, []);
        AssertCompositionValid(fixedDefinition, PreparedState(fixedDefinition, fixedHash, foldCurrent, finalizationPlan: fixedPlan));

        var foldedRange = ScheduleContractTestData.Disposition(
            1,
            2,
            firstScheduledLocal: foldCurrent.ScheduledLocal,
            lastScheduledLocal: foldNext.ScheduledLocal,
            firstScheduledAtUtc: foldCurrent.ScheduledAtUtc,
            lastScheduledAtUtc: foldNext.ScheduledAtUtc);
        var foldedRangeValidation = ScheduleContractValidator.ValidateDispositionEvidence(foldedRange);
        Assert.True(foldedRangeValidation.IsValid, ScheduleContractTestData.Errors(foldedRangeValidation));

        var wrongFixed = foldNext with { ScheduledAtUtc = foldCurrent.ScheduledAtUtc.AddMinutes(30) };
        var wrongFixedPlan = new ScheduleFinalizationPlan(1, wrongFixed, null, null, []);
        AssertCompositionInvalid(
            fixedDefinition,
            PreparedState(fixedDefinition, fixedHash, foldCurrent, finalizationPlan: wrongFixedPlan),
            "state.pendingDelivery.finalizationPlan.nextOccurrence",
            "recurrence_successor_mismatch");

        var dailyDefinition = ScheduleContractTestData.Definition();
        var dailyHash = DefinitionHash(dailyDefinition);
        var dailyCurrent = ScheduleContractTestData.OccurrenceAt(1);
        var dailyNext = ScheduleContractTestData.OccurrenceAt(3);
        var skipped = ScheduleContractTestData.Disposition(2);
        var dailyPlan = new ScheduleFinalizationPlan(1, dailyNext, null, null, [skipped]);
        AssertCompositionValid(dailyDefinition, PreparedState(dailyDefinition, dailyHash, dailyCurrent, finalizationPlan: dailyPlan));

        var wrongDaily = dailyNext with { ScheduledLocal = dailyNext.ScheduledLocal.AddDays(1) };
        var wrongDailyPlan = new ScheduleFinalizationPlan(1, wrongDaily, null, null, [skipped]);
        AssertCompositionInvalid(
            dailyDefinition,
            PreparedState(dailyDefinition, dailyHash, dailyCurrent, finalizationPlan: wrongDailyPlan),
            "state.pendingDelivery.finalizationPlan.nextOccurrence",
            "recurrence_successor_mismatch");

        var weeklyDefinition = ScheduleContractTestData.Definition(recurrenceKind: ScheduleRecurrenceKind.Weekly);
        var weeklyHash = DefinitionHash(weeklyDefinition);
        var weeklyNext = ScheduleContractTestData.Occurrence(
            2,
            dailyCurrent.ScheduledLocal.AddDays(7),
            dailyCurrent.ScheduledAtUtc.AddDays(7));
        var weeklyPlan = new ScheduleFinalizationPlan(1, weeklyNext, null, null, []);
        AssertCompositionValid(weeklyDefinition, PreparedState(weeklyDefinition, weeklyHash, dailyCurrent, finalizationPlan: weeklyPlan));

        var onceDefinition = ScheduleContractTestData.Definition(
            recurrenceKind: ScheduleRecurrenceKind.Once,
            misfireKind: ScheduleMisfirePolicyKind.Skip,
            catchUpLimit: 0);
        var onceHash = DefinitionHash(onceDefinition);
        var exhausted = new ScheduleFinalizationPlan(1, null, null, null, []);
        AssertCompositionValid(onceDefinition, PreparedState(onceDefinition, onceHash, dailyCurrent, finalizationPlan: exhausted));
        AssertCompositionInvalid(
            onceDefinition,
            PreparedState(onceDefinition, onceHash, dailyCurrent),
            "state.pendingDelivery.finalizationPlan.nextOccurrence",
            "once_recurrence_not_exhausted");
    }

    [Fact]
    public void Definition_aware_composition_rejects_self_consistent_off_definition_time_zones_and_anchors()
    {
        var dailyDefinition = ScheduleContractTestData.Definition();
        var dailyHash = DefinitionHash(dailyDefinition);
        var offAnchorDaily = ScheduleContractTestData.Occurrence(
            2,
            ScheduleContractTestData.FirstLocal,
            ScheduleContractTestData.FirstUtc.AddDays(1));
        AssertCompositionInvalid(
            dailyDefinition,
            PreparedState(dailyDefinition, dailyHash, offAnchorDaily),
            "state.nextOccurrence.scheduledLocal",
            "recurrence_anchor_mismatch");

        var otherTimeZone = ScheduleContractTestData.TimeZone("America/New_York");
        var offZone = ScheduleContractTestData.Occurrence(timeZone: otherTimeZone);
        AssertCompositionInvalid(
            dailyDefinition,
            PreparedState(dailyDefinition, dailyHash, offZone),
            "state.nextOccurrence.timeZone",
            "definition_time_zone_mismatch");

        var offAnchorDisposition = ScheduleContractTestData.Disposition(1) with
        {
            FirstScheduledLocal = ScheduleContractTestData.FirstLocal.AddMinutes(1),
            LastScheduledLocal = ScheduleContractTestData.FirstLocal.AddMinutes(1),
        };
        AssertCompositionInvalid(
            dailyDefinition,
            PreparedState(dailyDefinition, dailyHash, ScheduleContractTestData.OccurrenceAt(2)) with
            {
                DispositionEvidence = [offAnchorDisposition],
            },
            "state.dispositionEvidence[0].firstScheduledLocal",
            "recurrence_anchor_mismatch");

        var third = ScheduleContractTestData.OccurrenceAt(3);
        var offAnchorPlanDisposition = ScheduleContractTestData.Disposition(2) with
        {
            FirstScheduledLocal = ScheduleContractTestData.FirstLocal.AddDays(1).AddMinutes(1),
            LastScheduledLocal = ScheduleContractTestData.FirstLocal.AddDays(1).AddMinutes(1),
        };
        var offAnchorPlan = new ScheduleFinalizationPlan(1, third, null, null, [offAnchorPlanDisposition]);
        AssertCompositionInvalid(
            dailyDefinition,
            PreparedState(
                dailyDefinition,
                dailyHash,
                ScheduleContractTestData.OccurrenceAt(1),
                finalizationPlan: offAnchorPlan),
            "state.pendingDelivery.finalizationPlan.dispositionEvidence[0].firstScheduledLocal",
            "recurrence_anchor_mismatch");

        var weeklyDefinition = ScheduleContractTestData.Definition(recurrenceKind: ScheduleRecurrenceKind.Weekly);
        var weeklyHash = DefinitionHash(weeklyDefinition);
        var offAnchorWeekly = ScheduleContractTestData.Occurrence(
            2,
            ScheduleContractTestData.FirstLocal.AddDays(6),
            ScheduleContractTestData.FirstUtc.AddDays(7));
        var offAnchorWeeklyNext = ScheduleContractTestData.Occurrence(
            3,
            offAnchorWeekly.ScheduledLocal.AddDays(7),
            offAnchorWeekly.ScheduledAtUtc.AddDays(7));
        var offAnchorWeeklyPlan = new ScheduleFinalizationPlan(1, offAnchorWeeklyNext, null, null, []);
        AssertCompositionInvalid(
            weeklyDefinition,
            PreparedState(weeklyDefinition, weeklyHash, offAnchorWeekly, finalizationPlan: offAnchorWeeklyPlan),
            "state.nextOccurrence.scheduledLocal",
            "recurrence_anchor_mismatch");

        var onceDefinition = ScheduleContractTestData.Definition(
            recurrenceKind: ScheduleRecurrenceKind.Once,
            misfireKind: ScheduleMisfirePolicyKind.Skip,
            catchUpLimit: 0);
        var onceHash = DefinitionHash(onceDefinition);
        var secondOnce = ScheduleContractTestData.Occurrence(
            2,
            ScheduleContractTestData.FirstLocal,
            ScheduleContractTestData.FirstUtc.AddDays(1));
        var exhausted = new ScheduleFinalizationPlan(1, null, null, null, []);
        AssertCompositionInvalid(
            onceDefinition,
            PreparedState(onceDefinition, onceHash, secondOnce, finalizationPlan: exhausted),
            "state.nextOccurrence.scheduledLocal",
            "recurrence_anchor_mismatch");

        var fixedDefinition = ScheduleContractTestData.Definition(
            recurrenceKind: ScheduleRecurrenceKind.FixedInterval,
            fixedIntervalSeconds: 3600);
        var fixedHash = DefinitionHash(fixedDefinition);
        var offAnchorFixed = ScheduleContractTestData.Occurrence(
            scheduledLocal: ScheduleContractTestData.FirstLocal.AddMinutes(1));
        var fixedSuccessor = ScheduleContractTestData.Occurrence(
            2,
            offAnchorFixed.ScheduledLocal,
            offAnchorFixed.ScheduledAtUtc.AddHours(1));
        var fixedPlan = new ScheduleFinalizationPlan(1, fixedSuccessor, null, null, []);
        AssertCompositionInvalid(
            fixedDefinition,
            PreparedState(fixedDefinition, fixedHash, offAnchorFixed, finalizationPlan: fixedPlan),
            "state.nextOccurrence.scheduledLocal",
            "recurrence_anchor_mismatch");
    }

    [Theory]
    [InlineData(ScheduleRecurrenceKind.FixedInterval)]
    [InlineData(ScheduleRecurrenceKind.Daily)]
    [InlineData(ScheduleRecurrenceKind.Weekly)]
    public void Recurring_composition_requires_a_successor_before_explicit_exhaustion(ScheduleRecurrenceKind kind)
    {
        var definition = ScheduleContractTestData.Definition(
            recurrenceKind: kind,
            fixedIntervalSeconds: kind == ScheduleRecurrenceKind.FixedInterval ? 3600 : null);
        var definitionHash = DefinitionHash(definition);
        var exhausted = new ScheduleFinalizationPlan(1, null, null, null, []);

        AssertCompositionInvalid(
            definition,
            PreparedState(definition, definitionHash, ScheduleContractTestData.Occurrence(), finalizationPlan: exhausted),
            "state.pendingDelivery.finalizationPlan.nextOccurrence",
            "recurrence_successor_required");
    }

    [Fact]
    public void Recurring_composition_accepts_null_successor_only_at_bounded_exhaustion()
    {
        var exhausted = new ScheduleFinalizationPlan(1, null, null, null, []);

        var fixedDefinition = ScheduleContractTestData.Definition(
            recurrenceKind: ScheduleRecurrenceKind.FixedInterval,
            fixedIntervalSeconds: 3600);
        var fixedHash = DefinitionHash(fixedDefinition);
        var fixedBoundary = ScheduleContractTestData.Occurrence(
            scheduledAtUtc: new DateTimeOffset(9998, 12, 31, 23, 59, 0, TimeSpan.Zero));
        AssertCompositionValid(
            fixedDefinition,
            PreparedState(fixedDefinition, fixedHash, fixedBoundary, finalizationPlan: exhausted));

        var lastLocal = new DateTime(9998, 12, 31, 23, 0, 0, DateTimeKind.Unspecified);
        var dailyDefinition = ScheduleContractTestData.Definition() with
        {
            Recurrence = new ScheduleRecurrenceRule(ScheduleRecurrenceKind.Daily, lastLocal, null),
        };
        var dailyHash = DefinitionHash(dailyDefinition);
        var dailyBoundary = ScheduleContractTestData.Occurrence(scheduledLocal: lastLocal);
        AssertCompositionValid(
            dailyDefinition,
            PreparedState(dailyDefinition, dailyHash, dailyBoundary, finalizationPlan: exhausted));

        var weeklyDefinition = ScheduleContractTestData.Definition(recurrenceKind: ScheduleRecurrenceKind.Weekly) with
        {
            Recurrence = new ScheduleRecurrenceRule(ScheduleRecurrenceKind.Weekly, lastLocal, null),
        };
        var weeklyHash = DefinitionHash(weeklyDefinition);
        var weeklyBoundary = ScheduleContractTestData.Occurrence(scheduledLocal: lastLocal);
        AssertCompositionValid(
            weeklyDefinition,
            PreparedState(weeklyDefinition, weeklyHash, weeklyBoundary, finalizationPlan: exhausted));
    }

    [Fact]
    public void Definition_aware_composition_binds_historical_disposition_and_terminal_time_zones()
    {
        var definition = ScheduleContractTestData.Definition();
        var definitionHash = DefinitionHash(definition);
        var current = ScheduleContractTestData.OccurrenceAt(3);
        var firstDisposition = ScheduleContractTestData.Disposition(1);
        var secondOccurrence = ScheduleContractTestData.OccurrenceAt(2);
        var secondTerminal = ScheduleContractTestData.Terminal(secondOccurrence) with
        {
            Identity = ScheduleContractTestData.Identity(
                secondOccurrence,
                definitionHash,
                definition.Revision,
                definition.ScheduleId),
        };
        var baseline = PreparedState(definition, definitionHash, current) with
        {
            DispositionEvidence = [firstDisposition],
            TerminalDeliveryEvidence = [secondTerminal],
        };
        AssertCompositionValid(definition, baseline);

        var otherTimeZone = ScheduleContractTestData.TimeZone("America/New_York");
        AssertCompositionInvalid(
            definition,
            baseline with { DispositionEvidence = [firstDisposition with { TimeZone = otherTimeZone }] },
            "state.dispositionEvidence[0].timeZone",
            "definition_time_zone_mismatch");

        var offZoneOccurrence = secondOccurrence with { TimeZone = otherTimeZone };
        var offZoneTerminal = ScheduleContractTestData.Terminal(offZoneOccurrence) with
        {
            Identity = ScheduleContractTestData.Identity(
                offZoneOccurrence,
                definitionHash,
                definition.Revision,
                definition.ScheduleId),
        };
        AssertCompositionInvalid(
            definition,
            baseline with { TerminalDeliveryEvidence = [offZoneTerminal] },
            "state.terminalDeliveryEvidence[0].occurrence.timeZone",
            "definition_time_zone_mismatch");
    }

    [Fact]
    public void Definition_aware_composition_enforces_structurally_decidable_schedule_policies()
    {
        var defaultDefinition = ScheduleContractTestData.Definition();
        var defaultHash = DefinitionHash(defaultDefinition);
        var second = ScheduleContractTestData.OccurrenceAt(2);
        var secondState = PreparedState(defaultDefinition, defaultHash, second);

        var invalidLocal = ScheduleContractTestData.Disposition(
            1,
            disposition: ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped);
        AssertCompositionInvalid(
            defaultDefinition,
            secondState with { DispositionEvidence = [invalidLocal] },
            "state.dispositionEvidence[0].disposition",
            "disposition_policy_mismatch");

        var overlapSkipped = ScheduleContractTestData.Disposition(
            1,
            disposition: ScheduleOccurrenceDisposition.OverlapSkipped);
        AssertCompositionInvalid(
            defaultDefinition,
            secondState with { DispositionEvidence = [overlapSkipped] },
            "state.dispositionEvidence[0].disposition",
            "disposition_policy_mismatch");

        var allowDefinition = defaultDefinition with { Overlap = ScheduleOverlapPolicy.Allow };
        var allowHash = DefinitionHash(allowDefinition);
        var historicalDeferral = ScheduleContractTestData.Disposition(
            1,
            disposition: ScheduleOccurrenceDisposition.OverlapDeferred);
        AssertCompositionInvalid(
            allowDefinition,
            PreparedState(allowDefinition, allowHash, second) with { DispositionEvidence = [historicalDeferral] },
            "state.dispositionEvidence[0].disposition",
            "disposition_policy_mismatch");

        var skipDefinition = defaultDefinition with { Overlap = ScheduleOverlapPolicy.Skip };
        var skipHash = DefinitionHash(skipDefinition);
        var first = ScheduleContractTestData.OccurrenceAt(1);
        var deferredSecond = new ScheduleDeferredOccurrence(
            1,
            second,
            ScheduleContractTestData.Identity(second, skipHash, skipDefinition.Revision, skipDefinition.ScheduleId),
            second.ScheduledAtUtc.AddSeconds(1));
        var retainedDeferral = ScheduleContractTestData.Disposition(
            2,
            disposition: ScheduleOccurrenceDisposition.OverlapDeferred);
        var deferredPlan = new ScheduleFinalizationPlan(1, second, null, deferredSecond, [retainedDeferral]);
        var deferredState = PreparedState(skipDefinition, skipHash, first, finalizationPlan: deferredPlan);
        AssertCompositionInvalid(
            skipDefinition,
            deferredState,
            "state.pendingDelivery.finalizationPlan.deferredOccurrence",
            "overlap_policy_mismatch");
        AssertCompositionInvalid(
            skipDefinition,
            deferredState,
            "state.pendingDelivery.finalizationPlan.dispositionEvidence[0].disposition",
            "disposition_policy_mismatch");

        var latestDefinition = ScheduleContractTestData.Definition(
            misfireKind: ScheduleMisfirePolicyKind.FireLatestOnce,
            catchUpLimit: 0);
        var latestHash = DefinitionHash(latestDefinition);
        var latestEpisode = new ScheduleCatchUpEpisode(1, 1, 1);
        AssertCompositionInvalid(
            latestDefinition,
            PreparedState(latestDefinition, latestHash, first) with { CatchUpEpisode = latestEpisode },
            "state.catchUpEpisode",
            "catch_up_policy_mismatch");

        var boundedDefinition = ScheduleContractTestData.Definition(catchUpLimit: 1);
        var boundedHash = DefinitionHash(boundedDefinition);
        var overLimitPlan = new ScheduleFinalizationPlan(
            1,
            second,
            new ScheduleCatchUpEpisode(1, 3, 2),
            null,
            []);
        var overLimitState = PreparedState(boundedDefinition, boundedHash, first, finalizationPlan: overLimitPlan) with
        {
            CatchUpEpisode = new ScheduleCatchUpEpisode(1, 3, 3),
        };
        AssertCompositionInvalid(
            boundedDefinition,
            overLimitState,
            "state.catchUpEpisode",
            "catch_up_policy_mismatch");
        AssertCompositionInvalid(
            boundedDefinition,
            overLimitState,
            "state.pendingDelivery.finalizationPlan.catchUpEpisode",
            "catch_up_policy_mismatch");

        var matchingPolicies = defaultDefinition with
        {
            DaylightSaving = new ScheduleDaylightSavingPolicy(
                ScheduleInvalidLocalTimePolicy.Skip,
                ScheduleAmbiguousLocalTimePolicy.EarlierUtc),
            Overlap = ScheduleOverlapPolicy.Skip,
        };
        var matchingHash = DefinitionHash(matchingPolicies);
        var matchingState = PreparedState(
            matchingPolicies,
            matchingHash,
            ScheduleContractTestData.OccurrenceAt(3)) with
        {
            DispositionEvidence =
            [
                invalidLocal,
                ScheduleContractTestData.Disposition(
                    2,
                    disposition: ScheduleOccurrenceDisposition.OverlapSkipped),
            ],
        };
        AssertCompositionValid(matchingPolicies, matchingState);

        var matchingCatchUp = ScheduleContractTestData.Definition(catchUpLimit: 2);
        var matchingCatchUpHash = DefinitionHash(matchingCatchUp);
        var matchingCatchUpPlan = new ScheduleFinalizationPlan(
            1,
            second,
            new ScheduleCatchUpEpisode(1, 3, 1),
            null,
            []);
        var matchingCatchUpState = PreparedState(
            matchingCatchUp,
            matchingCatchUpHash,
            first,
            finalizationPlan: matchingCatchUpPlan) with
        {
            CatchUpEpisode = new ScheduleCatchUpEpisode(1, 3, 2),
        };
        AssertCompositionValid(matchingCatchUp, matchingCatchUpState);
    }

    [Fact]
    public void Successor_gaps_require_final_skip_coverage_and_overlap_deferral_retains_the_exact_next_identity()
    {
        var current = ScheduleContractTestData.OccurrenceAt(1);
        var prepared = ScheduleContractTestData.Prepared(current);
        var third = ScheduleContractTestData.OccurrenceAt(3);
        var uncovered = new ScheduleFinalizationPlan(1, third, null, null, []);
        AssertPendingInvalid(
            ScheduleContractTestData.Pending(current, prepared, finalizationPlan: uncovered),
            "finalizationPlan.dispositionEvidence",
            "successor_gap_not_covered");

        var deferredThird = ScheduleContractTestData.Deferred(third);
        var onlyDeferred = ScheduleContractTestData.Disposition(
            2,
            disposition: ScheduleOccurrenceDisposition.OverlapDeferred);
        var deferredGap = new ScheduleFinalizationPlan(1, third, null, deferredThird, [onlyDeferred]);
        AssertPendingInvalid(
            ScheduleContractTestData.Pending(current, prepared, finalizationPlan: deferredGap),
            "finalizationPlan.dispositionEvidence",
            "successor_gap_not_covered");

        var second = ScheduleContractTestData.OccurrenceAt(2);
        var retained = ScheduleContractTestData.Deferred(second);
        var retainedEvidence = ScheduleContractTestData.Disposition(
            2,
            disposition: ScheduleOccurrenceDisposition.OverlapDeferred);
        var retainedPlan = new ScheduleFinalizationPlan(1, second, null, retained, [retainedEvidence]);
        var retainedPending = ScheduleContractTestData.Pending(current, prepared, finalizationPlan: retainedPlan);
        var retainedState = ScheduleContractTestData.State(current, retainedPending);
        var validation = ScheduleContractValidator.ValidateState(retainedState);
        Assert.True(validation.IsValid, ScheduleContractTestData.Errors(validation));

        var unretained = new ScheduleFinalizationPlan(1, second, null, null, [retainedEvidence]);
        var unretainedValidation = ScheduleContractValidator.ValidateFinalizationPlan(unretained);
        Assert.Contains(
            unretainedValidation.Errors,
            error => error.Path == "dispositionEvidence[0]" && error.Code == "unretained_overlap_deferral");

        var multiple = new ScheduleFinalizationPlan(1, second, null, retained, [retainedEvidence, retainedEvidence]);
        var multipleValidation = ScheduleContractValidator.ValidateFinalizationPlan(multiple);
        Assert.Contains(
            multipleValidation.Errors,
            error => error.Path == "dispositionEvidence" && error.Code == "multiple_overlap_deferrals");
    }

    [Fact]
    public void Terminal_history_is_unique_conclusive_and_disjoint_from_live_or_finally_skipped_occurrences()
    {
        var occurrence = ScheduleContractTestData.OccurrenceAt(1);
        var backpressured = ScheduleContractTestData.Terminal(occurrence, ScheduleDeliveryResultKind.Backpressured);
        AssertTerminalInvalid(backpressured, "result.kind", "nonterminal_delivery_result");

        var prepared = ScheduleContractTestData.Prepared(occurrence);
        var observed = ScheduleContractTestData.Pending(
            occurrence,
            prepared,
            ScheduleContractTestData.Result(prepared.CanonicalEnvelopeHash, ScheduleDeliveryResultKind.Backpressured));
        Assert.True(ScheduleContractValidator.ValidatePendingDelivery(observed).IsValid);

        var beforeOccurrence = ScheduleContractTestData.Terminal(occurrence) with
        {
            Result = ScheduleContractTestData.Result(
                new string('7', ScheduleContractLimits.Sha256HexCharacters),
                recordedAtUtc: occurrence.ScheduledAtUtc.AddTicks(-1)),
        };
        AssertTerminalInvalid(beforeOccurrence, "result.recordedAtUtc", "result_before_occurrence");

        var queued = ScheduleContractTestData.Terminal(occurrence);
        var replayed = ScheduleContractTestData.Terminal(occurrence, ScheduleDeliveryResultKind.Replayed);
        var duplicates = ScheduleContractTestData.State(
            next: ScheduleContractTestData.OccurrenceAt(2),
            terminal: [queued, replayed]);
        AssertStateInvalid(duplicates, "terminalDeliveryEvidence[1].identity", "duplicate_terminal_identity");

        var skipped = ScheduleContractTestData.State(
            next: ScheduleContractTestData.OccurrenceAt(2),
            dispositions: [ScheduleContractTestData.Disposition(1)],
            terminal: [queued]);
        AssertStateInvalid(skipped, "terminalDeliveryEvidence[0].occurrence", "terminal_occurrence_already_disposed");

        var historicalDeferral = ScheduleContractTestData.State(
            next: ScheduleContractTestData.OccurrenceAt(2),
            dispositions:
            [
                ScheduleContractTestData.Disposition(
                    1,
                    disposition: ScheduleOccurrenceDisposition.OverlapDeferred),
            ],
            terminal: [queued]);
        var historicalValidation = ScheduleContractValidator.ValidateState(historicalDeferral);
        Assert.True(historicalValidation.IsValid, ScheduleContractTestData.Errors(historicalValidation));
    }

    [Fact]
    public void State_dispositions_are_historical_or_retain_one_exact_active_overlap_deferral()
    {
        var next = ScheduleContractTestData.OccurrenceAt(2);
        var finalAtNext = ScheduleContractTestData.State(
            next,
            dispositions: [ScheduleContractTestData.Disposition(2)]);
        AssertStateInvalid(
            finalAtNext,
            "dispositionEvidence[0]",
            "final_disposition_not_predecessor");

        var current = ScheduleContractTestData.OccurrenceAt(1);
        var pending = ScheduleContractTestData.Pending(current);
        var coversPending = ScheduleContractTestData.State(
            current,
            pending,
            dispositions: [ScheduleContractTestData.Disposition(1)]);
        AssertStateInvalid(
            coversPending,
            "dispositionEvidence[0]",
            "final_disposition_covers_pending");

        var activeEvidence = ScheduleContractTestData.Disposition(
            next.Ordinal,
            disposition: ScheduleOccurrenceDisposition.OverlapDeferred);
        var unretained = ScheduleContractTestData.State(next, dispositions: [activeEvidence]);
        AssertStateInvalid(
            unretained,
            "dispositionEvidence[0]",
            "unretained_state_overlap_deferral");

        var deferred = ScheduleContractTestData.Deferred(next);
        var active = ScheduleContractTestData.State(
            next,
            dispositions: [activeEvidence],
            deferred: deferred);
        var activeValidation = ScheduleContractValidator.ValidateState(active);
        Assert.True(activeValidation.IsValid, ScheduleContractTestData.Errors(activeValidation));

        AssertStateInvalid(
            active with { DispositionEvidence = [] },
            "deferredOccurrence",
            "active_overlap_deferral_evidence_required");
    }

    [Fact]
    public void State_clock_bounds_every_durable_evidence_timestamp_and_null_shapes_fail_closed()
    {
        var occurrence = ScheduleContractTestData.Occurrence();
        var prepared = ScheduleContractTestData.Prepared(occurrence);
        var earlyResult = ScheduleContractTestData.Result(
            prepared.CanonicalEnvelopeHash,
            recordedAtUtc: prepared.PreparedAtUtc.AddTicks(-1));
        AssertPendingInvalid(
            ScheduleContractTestData.Pending(occurrence, prepared, earlyResult),
            "result.recordedAtUtc",
            "result_before_prepared");

        var pending = ScheduleContractTestData.Pending(occurrence, prepared);
        AssertStateInvalid(
            ScheduleContractTestData.State(
                occurrence,
                pending,
                lastClockObservedAtUtc: prepared.PreparedAtUtc.AddTicks(-1)),
            "pendingDelivery.prepared.preparedAtUtc",
            "evidence_after_clock");

        var deferred = ScheduleContractTestData.Deferred(occurrence);
        AssertStateInvalid(
            ScheduleContractTestData.State(
                occurrence,
                deferred: deferred,
                lastClockObservedAtUtc: deferred.DeferredAtUtc.AddTicks(-1)),
            "deferredOccurrence.deferredAtUtc",
            "evidence_after_clock");

        var disposition = ScheduleContractTestData.Disposition(1);
        AssertStateInvalid(
            ScheduleContractTestData.State(
                ScheduleContractTestData.OccurrenceAt(2),
                dispositions: [disposition],
                lastClockObservedAtUtc: disposition.RecordedAtUtc.AddTicks(-1)),
            "dispositionEvidence[0].recordedAtUtc",
            "evidence_after_clock");

        var terminal = ScheduleContractTestData.Terminal(occurrence);
        AssertStateInvalid(
            ScheduleContractTestData.State(
                ScheduleContractTestData.OccurrenceAt(2),
                lastClockObservedAtUtc: terminal.FinalizedAtUtc.AddTicks(-1),
                terminal: [terminal]),
            "terminalDeliveryEvidence[0].finalizedAtUtc",
            "evidence_after_clock");
        AssertStateInvalid(
            ScheduleContractTestData.State(
                ScheduleContractTestData.OccurrenceAt(2),
                lastClockObservedAtUtc: null,
                terminal: [terminal]) with
            {
                LastClockObservedAtUtc = null,
            },
            "lastClockObservedAtUtc",
            "evidence_clock_required");

        var nullDisposition = ScheduleContractTestData.State() with
        {
            DispositionEvidence = new ScheduleOccurrenceDispositionEvidence[] { null! },
        };
        AssertStateInvalid(nullDisposition, "dispositionEvidence[0]", "required");
        var nullTerminal = ScheduleContractTestData.State() with
        {
            TerminalDeliveryEvidence = new ScheduleTerminalDeliveryEvidence[] { null! },
        };
        AssertStateInvalid(nullTerminal, "terminalDeliveryEvidence[0]", "required");
        var nullPlanItem = new ScheduleFinalizationPlan(
            1,
            ScheduleContractTestData.OccurrenceAt(2),
            null,
            null,
            new ScheduleOccurrenceDispositionEvidence[] { null! });
        var nullPlanState = ScheduleContractTestData.State(
            occurrence,
            ScheduleContractTestData.Pending(occurrence, prepared, finalizationPlan: nullPlanItem));
        AssertStateInvalid(
            nullPlanState,
            "pendingDelivery.finalizationPlan.dispositionEvidence[0]",
            "required");
        AssertStateInvalid(ScheduleContractTestData.State() with { DispositionEvidence = null! }, "dispositionEvidence", "required");
        AssertStateInvalid(ScheduleContractTestData.State() with { TerminalDeliveryEvidence = null! }, "terminalDeliveryEvidence", "required");
    }

    [Fact]
    public void Evidence_snapshots_read_at_most_limit_plus_one_from_hostile_lists()
    {
        var dispositions = new HostileReadOnlyList<ScheduleOccurrenceDispositionEvidence>(
            index => ScheduleContractTestData.Disposition(index + 1L));
        var dispositionState = ScheduleContractTestData.State(
            next: ScheduleContractTestData.OccurrenceAt(ScheduleContractLimits.MaxDispositionEvidenceItems + 2L)) with
        {
            DispositionEvidence = dispositions,
        };
        Assert.Equal(ScheduleContractLimits.MaxDispositionEvidenceItems + 1, dispositions.AccessCount);
        AssertStateInvalid(dispositionState, "dispositionEvidence", "evidence_limit_exceeded");

        var terminal = new HostileReadOnlyList<ScheduleTerminalDeliveryEvidence>(
            index => ScheduleContractTestData.Terminal(ScheduleContractTestData.OccurrenceAt(index + 1L)));
        var terminalState = ScheduleContractTestData.State(
            next: ScheduleContractTestData.OccurrenceAt(ScheduleContractLimits.MaxTerminalDeliveryEvidenceItems + 2L)) with
        {
            TerminalDeliveryEvidence = terminal,
        };
        Assert.Equal(ScheduleContractLimits.MaxTerminalDeliveryEvidenceItems + 1, terminal.AccessCount);
        AssertStateInvalid(terminalState, "terminalDeliveryEvidence", "evidence_limit_exceeded");

        var planned = new HostileReadOnlyList<ScheduleOccurrenceDispositionEvidence>(
            index => ScheduleContractTestData.Disposition(index + 1L));
        var plan = new ScheduleFinalizationPlan(
            1,
            ScheduleContractTestData.OccurrenceAt(ScheduleContractLimits.MaxFinalizationEvidenceItems + 2L),
            null,
            null,
            planned);
        Assert.Equal(ScheduleContractLimits.MaxFinalizationEvidenceItems + 1, planned.AccessCount);
        var planValidation = ScheduleContractValidator.ValidateFinalizationPlan(plan);
        Assert.Contains(planValidation.Errors, error => error.Path == "dispositionEvidence" && error.Code == "evidence_limit_exceeded");
    }

    private static ScheduleState PreparedState(
        ScheduleDefinition definition,
        string definitionHash,
        ScheduleOccurrence occurrence,
        ScheduleFinalizationPlan? finalizationPlan = null,
        TriggerLoopReference? target = null,
        TriggerAdapterReference? adapter = null,
        TriggerActorContext? actorContext = null,
        TriggerAuthorityEvidence? authority = null,
        TriggerPayloadEvidence? payload = null,
        TriggerTemporalEvidence? temporal = null,
        TriggerRedeliveryEvidence? redelivery = null,
        bool publicationRequested = false,
        CustomLoopConversationReference? conversation = null)
    {
        var prepared = ScheduleContractTestData.Prepared(
            occurrence,
            definitionHash: definitionHash,
            definitionRevision: definition.Revision,
            scheduleId: definition.ScheduleId,
            target: target,
            adapter: adapter,
            actorContext: actorContext,
            authority: authority,
            payload: payload,
            temporal: temporal,
            redelivery: redelivery,
            publicationRequested: publicationRequested,
            conversation: conversation);
        var pending = ScheduleContractTestData.Pending(
            occurrence,
            prepared,
            finalizationPlan: finalizationPlan,
            definitionHash: definitionHash,
            definitionRevision: definition.Revision,
            scheduleId: definition.ScheduleId);
        return ScheduleContractTestData.State(
            occurrence,
            pending,
            definitionRevision: definition.Revision,
            definitionHash: definitionHash,
            scheduleId: definition.ScheduleId);
    }

    private static string DefinitionHash(ScheduleDefinition definition)
    {
        Assert.True(
            ScheduleContractHash.TryComputeDefinition(definition, out var hash, out var validation),
            ScheduleContractTestData.Errors(validation));
        return hash!;
    }

    private static void AssertCompositionValid(ScheduleDefinition definition, ScheduleState state)
    {
        var validation = ScheduleContractValidator.ValidatePreparedDeliveryComposition(definition, state);
        Assert.True(validation.IsValid, ScheduleContractTestData.Errors(validation));
    }

    private static void AssertDefinitionStateValid(ScheduleDefinition definition, ScheduleState state)
    {
        var validation = ScheduleContractValidator.ValidateDefinitionStateComposition(definition, state);
        Assert.True(validation.IsValid, ScheduleContractTestData.Errors(validation));
    }

    private static void AssertDefinitionStateInvalid(
        ScheduleDefinition definition,
        ScheduleState state,
        string path,
        string code)
    {
        var validation = ScheduleContractValidator.ValidateDefinitionStateComposition(definition, state);
        Assert.Contains(validation.Errors, error => error.Path == path && error.Code == code);
    }

    private static void AssertCompositionInvalid(
        ScheduleDefinition definition,
        ScheduleState state,
        string path,
        string code)
    {
        var validation = ScheduleContractValidator.ValidatePreparedDeliveryComposition(definition, state);
        Assert.Contains(validation.Errors, error => error.Path == path && error.Code == code);
    }

    private static void AssertPendingInvalid(SchedulePendingDelivery pending, string path, string code)
    {
        var validation = ScheduleContractValidator.ValidatePendingDelivery(pending);
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

    private sealed class HostileReadOnlyList<T>(Func<int, T> factory) : IReadOnlyList<T>
    {
        public int Count => int.MaxValue;

        public int AccessCount { get; private set; }

        public T this[int index]
        {
            get
            {
                AccessCount++;
                return factory(index);
            }
        }

        public IEnumerator<T> GetEnumerator()
            => throw new InvalidOperationException("The bounded snapshot must not enumerate an attacker-sized source.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
