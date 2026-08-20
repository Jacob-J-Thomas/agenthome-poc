using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Tests.Triggers.Schedules;

internal static class ScheduleContractTestData
{
    internal const string DefinitionHash = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    internal const string RulesFingerprint = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    internal static readonly DateTime FirstLocal = new(2026, 8, 12, 9, 30, 0, DateTimeKind.Unspecified);
    internal static readonly DateTimeOffset FirstUtc = new(2026, 8, 12, 14, 30, 0, TimeSpan.Zero);

    internal static ScheduleDefinition Definition(
        long revision = 1,
        ScheduleRecurrenceKind recurrenceKind = ScheduleRecurrenceKind.Daily,
        long? fixedIntervalSeconds = null,
        ScheduleMisfirePolicyKind misfireKind = ScheduleMisfirePolicyKind.CatchUp,
        int catchUpLimit = 3,
        bool enabled = true)
    {
        Assert.True(ScheduleId.TryParse("daily-reflection", out var scheduleId));
        Assert.True(AuthorityActorId.TryParse("owner", out var actorId, out _));
        Assert.True(AuthorityProfileId.TryParse("trigger-operator", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("7", out var profileRevision, out _));
        return new ScheduleDefinition(
            ScheduleDefinition.CurrentSchemaVersion,
            scheduleId!,
            revision,
            Target(),
            TriggerDeliveryTestData.Adapter("org.embodysense/triggers/time", implementation: "triggers/time"),
            actorId!,
            "scheduler",
            "workspace-1",
            "operator",
            new AuthorityProfileReference(profileId!, profileRevision!),
            new SchedulePayloadReference("payload/daily-reflection", CapabilityIntegrityDigest.Compute([1, 2, 3, 4])),
            SchedulePriority.Normal,
            new ScheduleRecurrenceRule(recurrenceKind, FirstLocal, fixedIntervalSeconds),
            TimeZone(),
            new ScheduleDaylightSavingPolicy(ScheduleInvalidLocalTimePolicy.ShiftForward, ScheduleAmbiguousLocalTimePolicy.EarlierUtc),
            new ScheduleMisfirePolicy(misfireKind, catchUpLimit),
            ScheduleOverlapPolicy.DeferOne,
            enabled);
    }

    internal static TriggerLoopReference Target()
        => TriggerDeliveryTestData.GovernedLoop(
            "daily-reflection",
            "revision-8",
            'a',
            "publish-daily-reflection-8",
            'b',
            "daily-reflection-grant",
            4,
            'c');

    internal static ScheduleTimeZoneReference TimeZone(string id = "America/Chicago", string? rulesFingerprint = null)
        => new(id, rulesFingerprint ?? RulesFingerprint);

    internal static ScheduleOccurrence Occurrence(
        long ordinal = 1,
        DateTime? scheduledLocal = null,
        DateTimeOffset? scheduledAtUtc = null,
        ScheduleTimeZoneReference? timeZone = null)
        => new(
            ScheduleOccurrence.CurrentSchemaVersion,
            ordinal,
            scheduledLocal ?? FirstLocal,
            scheduledAtUtc ?? FirstUtc,
            timeZone ?? TimeZone());

    internal static ScheduleOccurrence OccurrenceAt(long ordinal)
        => Occurrence(
            ordinal,
            FirstLocal.AddDays(ordinal - 1),
            FirstUtc.AddDays(ordinal - 1));

    internal static SchedulePendingDelivery Pending(
        ScheduleOccurrence? occurrence = null,
        SchedulePreparedDelivery? prepared = null,
        ScheduleDeliveryResultEvidence? result = null,
        ScheduleFinalizationPlan? finalizationPlan = null,
        DateTimeOffset? claimedAtUtc = null,
        string definitionHash = DefinitionHash,
        long definitionRevision = 1,
        ScheduleId? scheduleId = null,
        string? overlapEvidenceHash = null)
    {
        occurrence ??= Occurrence();
        Assert.True(ScheduleClaimId.TryParse("claim-daily-reflection-1", out var claimId));
        var identity = Identity(occurrence, definitionHash, definitionRevision, scheduleId);
        var phase = result is not null
            ? SchedulePendingDeliveryPhase.ResultObserved
            : prepared is not null
                ? SchedulePendingDeliveryPhase.Prepared
                : SchedulePendingDeliveryPhase.Claimed;
        if (prepared is not null)
        {
            finalizationPlan ??= FinalizationPlan(occurrence);
        }

        return new SchedulePendingDelivery(
            SchedulePendingDelivery.CurrentSchemaVersion,
            phase,
            occurrence,
            identity,
            claimId!,
            claimedAtUtc ?? occurrence.ScheduledAtUtc,
            prepared is null ? null : new string('f', ScheduleContractLimits.Sha256HexCharacters),
            prepared is null ? null : new string('9', ScheduleContractLimits.Sha256HexCharacters),
            prepared is null ? null : overlapEvidenceHash ?? new string('8', ScheduleContractLimits.Sha256HexCharacters),
            finalizationPlan,
            prepared,
            result);
    }

    internal static ScheduleOccurrenceIdentity Identity(
        ScheduleOccurrence occurrence,
        string definitionHash = DefinitionHash,
        long definitionRevision = 1,
        ScheduleId? scheduleId = null)
    {
        if (scheduleId is null)
        {
            Assert.True(ScheduleId.TryParse("daily-reflection", out scheduleId));
        }

        Assert.True(ScheduleIdentityDerivation.TryDerive(scheduleId, definitionRevision, definitionHash, occurrence, out var identity, out var validation), Errors(validation));
        return identity!;
    }

    internal static SchedulePreparedDelivery Prepared(
        ScheduleOccurrence? occurrence = null,
        DateTimeOffset? preparedAtUtc = null,
        TriggerKind kind = TriggerKind.Time,
        bool referencedPayload = false,
        bool admitted = false,
        string definitionHash = DefinitionHash,
        long definitionRevision = 1,
        ScheduleId? scheduleId = null,
        TriggerLoopReference? target = null,
        TriggerAdapterReference? adapter = null,
        TriggerActorContext? actorContext = null,
        TriggerAuthorityEvidence? authority = null,
        TriggerPayloadEvidence? payload = null,
        TriggerTemporalEvidence? temporal = null,
        TriggerRedeliveryEvidence? redelivery = null,
        ScheduleOverlapPolicy overlap = ScheduleOverlapPolicy.DeferOne,
        string? overlapEvidenceHash = null,
        bool publicationRequested = false,
        CustomLoopConversationReference? conversation = null,
        Func<TriggerDeliveryEnvelope, TriggerDeliveryEnvelope>? transform = null)
    {
        var pending = Pending(
            occurrence,
            definitionHash: definitionHash,
            definitionRevision: definitionRevision,
            scheduleId: scheduleId);
        var created = pending.Occurrence.ScheduledAtUtc;
        var validTarget = target ?? Target();
        var validAdapter = adapter ?? TriggerDeliveryTestData.Adapter("org.embodysense/triggers/time", implementation: "triggers/time");
        var validActor = actorContext ?? TriggerDeliveryTestData.ActorContext(surface: "scheduler");
        var validAuthority = authority ?? TriggerDeliveryTestData.Authority(evaluatedAtUtc: created.AddSeconds(2));
        var validTemporal = temporal ?? TriggerDeliveryTestData.Temporal(
                createdAtUtc: created,
                observedAtUtc: created.AddSeconds(1),
                receivedAtUtc: created.AddSeconds(2),
                admittedAtUtc: admitted ? created.AddSeconds(3) : null);
        var validPayload = payload ?? (referencedPayload
                ? TriggerDeliveryTestData.ReferencedPayload("payload/daily-reflection", [1, 2, 3, 4])
                : TriggerDeliveryTestData.InlinePayload([1, 2, 3, 4]));
        if (redelivery is null)
        {
            Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(
                1,
                1,
                pending.Identity.DeliveryId,
                out redelivery,
                out _));
        }

        TriggerDeliveryEnvelope envelope;
        if (kind == TriggerKind.Time)
        {
            var directive = new ScheduleExecutionDirective(
                ScheduleExecutionDirective.CurrentSchemaVersion,
                scheduleId ?? ScheduleIdFromDefault(),
                definitionRevision,
                definitionHash,
                pending.Occurrence,
                pending.Identity,
                validTarget,
                overlap,
                overlapEvidenceHash ?? new string('8', ScheduleContractLimits.Sha256HexCharacters));
            Assert.True(TriggerDeliveryFactory.TryCreateScheduledEnvelope(
                TriggerDeliveryEnvelope.CurrentSchemaVersion,
                pending.Identity.DeliveryId,
                pending.Identity.DeduplicationId,
                validAdapter,
                validTarget,
                validActor,
                validAuthority,
                validTemporal,
                validPayload,
                redelivery,
                directive,
                publicationRequested,
                conversation,
                admitted ? TriggerAdmissionStatus.Admitted : TriggerAdmissionStatus.Unknown,
                admitted ? TriggerAdmissionReason.EvidenceAccepted : TriggerAdmissionReason.Unknown,
                out var scheduledEnvelope,
                out var scheduledValidation),
                string.Join(',', scheduledValidation.Errors.Select(error => $"{error.Field}:{error.Code}")));
            envelope = scheduledEnvelope!;
        }
        else
        {
            Assert.True(TriggerDeliveryFactory.TryCreateEnvelope(
                TriggerDeliveryEnvelope.CurrentSchemaVersion,
                pending.Identity.DeliveryId,
                pending.Identity.DeduplicationId,
                kind,
                validAdapter,
                validTarget,
                validActor,
                validAuthority,
                validTemporal,
                validPayload,
                redelivery,
                publicationRequested,
                conversation,
                admitted ? TriggerAdmissionStatus.Admitted : TriggerAdmissionStatus.Unknown,
                admitted ? TriggerAdmissionReason.EvidenceAccepted : TriggerAdmissionReason.Unknown,
                out var nonscheduledEnvelope,
                out var nonscheduledValidation),
                string.Join(',', nonscheduledValidation.Errors.Select(error => error.Code)));
            envelope = nonscheduledEnvelope!;
        }

        envelope = transform?.Invoke(envelope) ?? envelope;
        Assert.True(TriggerDeliveryHash.TryCompute(envelope, out var hash, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return new SchedulePreparedDelivery(
            SchedulePreparedDelivery.CurrentSchemaVersion,
            envelope,
            hash!,
            preparedAtUtc ?? created.AddSeconds(3));
    }

    private static ScheduleId ScheduleIdFromDefault()
    {
        Assert.True(ScheduleId.TryParse("daily-reflection", out var scheduleId));
        return scheduleId!;
    }

    internal static ScheduleDeliveryResultEvidence Result(
        string canonicalEnvelopeHash,
        ScheduleDeliveryResultKind kind = ScheduleDeliveryResultKind.Queued,
        DateTimeOffset? recordedAtUtc = null)
        => new(
            ScheduleDeliveryResultEvidence.CurrentSchemaVersion,
            kind,
            "queue-accepted",
            canonicalEnvelopeHash,
            recordedAtUtc ?? FirstUtc.AddSeconds(4));

    internal static ScheduleOccurrenceDispositionEvidence Disposition(
        long firstOrdinal,
        long? lastOrdinal = null,
        ScheduleOccurrenceDisposition disposition = ScheduleOccurrenceDisposition.MisfireSkipped,
        DateTime? firstScheduledLocal = null,
        DateTime? lastScheduledLocal = null,
        DateTimeOffset? firstScheduledAtUtc = null,
        DateTimeOffset? lastScheduledAtUtc = null,
        string reason = "misfire-window-exceeded",
        string? decisionEvidenceHash = null)
    {
        var last = lastOrdinal ?? firstOrdinal;
        var firstLocal = firstScheduledLocal ?? FirstLocal.AddDays(firstOrdinal - 1);
        var lastLocal = lastScheduledLocal ?? firstLocal.AddDays(last - firstOrdinal);
        DateTimeOffset? firstUtc = disposition == ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped
            ? null
            : firstScheduledAtUtc ?? FirstUtc.AddDays(firstOrdinal - 1);
        DateTimeOffset? lastUtc = disposition == ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped
            ? null
            : lastScheduledAtUtc ?? firstUtc!.Value.AddDays(last - firstOrdinal);
        return new ScheduleOccurrenceDispositionEvidence(
            ScheduleOccurrenceDispositionEvidence.CurrentSchemaVersion,
            firstOrdinal,
            last,
            last - firstOrdinal + 1,
            firstLocal,
            lastLocal,
            firstUtc,
            lastUtc,
            TimeZone(),
            disposition,
            decisionEvidenceHash ?? (disposition is ScheduleOccurrenceDisposition.OverlapSkipped or ScheduleOccurrenceDisposition.OverlapDeferred
                ? new string('8', ScheduleContractLimits.Sha256HexCharacters)
                : null),
            reason,
            (lastUtc ?? FirstUtc.AddDays(last - 1)).AddSeconds(1));
    }

    internal static ScheduleFinalizationPlan FinalizationPlan(
        ScheduleOccurrence current,
        ScheduleOccurrence? next = null,
        ScheduleCatchUpEpisode? catchUp = null,
        ScheduleDeferredOccurrence? deferred = null,
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence>? dispositions = null)
        => new(
            ScheduleFinalizationPlan.CurrentSchemaVersion,
            next ?? Occurrence(
                current.Ordinal + 1,
                current.ScheduledLocal.AddDays(1),
                current.ScheduledAtUtc.AddDays(1),
                current.TimeZone),
            catchUp,
            deferred,
            dispositions ?? []);

    internal static ScheduleDeferredOccurrence Deferred(ScheduleOccurrence occurrence)
        => new(
            ScheduleDeferredOccurrence.CurrentSchemaVersion,
            occurrence,
            Identity(occurrence),
            occurrence.ScheduledAtUtc.AddSeconds(1));

    internal static ScheduleTerminalDeliveryEvidence Terminal(
        ScheduleOccurrence occurrence,
        ScheduleDeliveryResultKind kind = ScheduleDeliveryResultKind.Queued,
        DateTimeOffset? finalizedAtUtc = null)
    {
        var result = Result(new string('7', ScheduleContractLimits.Sha256HexCharacters), kind, occurrence.ScheduledAtUtc.AddSeconds(1));
        return new ScheduleTerminalDeliveryEvidence(
            ScheduleTerminalDeliveryEvidence.CurrentSchemaVersion,
            occurrence,
            Identity(occurrence),
            new string('f', ScheduleContractLimits.Sha256HexCharacters),
            new string('9', ScheduleContractLimits.Sha256HexCharacters),
            new string('8', ScheduleContractLimits.Sha256HexCharacters),
            result,
            finalizedAtUtc ?? occurrence.ScheduledAtUtc.AddSeconds(2));
    }

    internal static ScheduleState State(
        ScheduleOccurrence? next = null,
        SchedulePendingDelivery? pending = null,
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence>? dispositions = null,
        long definitionRevision = 1,
        string definitionHash = DefinitionHash,
        long stateRevision = 1,
        DateTimeOffset? lastClockObservedAtUtc = null,
        ScheduleCatchUpEpisode? catchUp = null,
        ScheduleDeferredOccurrence? deferred = null,
        IReadOnlyList<ScheduleTerminalDeliveryEvidence>? terminal = null,
        ScheduleId? scheduleId = null)
    {
        if (scheduleId is null)
        {
            Assert.True(ScheduleId.TryParse("daily-reflection", out scheduleId));
        }

        next ??= Occurrence();
        var observedClock = lastClockObservedAtUtc ?? LatestEvidenceTime(
            next.ScheduledAtUtc.AddSeconds(5),
            pending,
            deferred,
            dispositions,
            terminal);
        return new ScheduleState(
            ScheduleState.CurrentSchemaVersion,
            scheduleId!,
            definitionRevision,
            definitionHash,
            stateRevision,
            true,
            next,
            catchUp,
            deferred,
            observedClock,
            pending,
            dispositions ?? [],
            terminal ?? []);
    }

    private static DateTimeOffset LatestEvidenceTime(
        DateTimeOffset initial,
        SchedulePendingDelivery? pending,
        ScheduleDeferredOccurrence? deferred,
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence>? dispositions,
        IReadOnlyList<ScheduleTerminalDeliveryEvidence>? terminal)
    {
        var latest = initial;
        void Observe(DateTimeOffset value)
        {
            if (value > latest)
            {
                latest = value;
            }
        }

        if (pending is not null)
        {
            Observe(pending.ClaimedAtUtc);
            if (pending.Prepared is not null)
            {
                Observe(pending.Prepared.PreparedAtUtc);
            }

            if (pending.Result is not null)
            {
                Observe(pending.Result.RecordedAtUtc);
            }

            if (pending.FinalizationPlan?.DeferredOccurrence is not null)
            {
                Observe(pending.FinalizationPlan.DeferredOccurrence.DeferredAtUtc);
            }

            ObserveDispositionTimes(pending.FinalizationPlan?.DispositionEvidence);
        }

        if (deferred is not null)
        {
            Observe(deferred.DeferredAtUtc);
        }

        ObserveDispositionTimes(dispositions);
        if (terminal is not null)
        {
            for (var index = 0; index < Math.Min(terminal.Count, ScheduleContractLimits.MaxTerminalDeliveryEvidenceItems + 1); index++)
            {
                if (terminal[index]?.Result is not null)
                {
                    Observe(terminal[index].Result.RecordedAtUtc);
                }

                if (terminal[index] is not null)
                {
                    Observe(terminal[index].FinalizedAtUtc);
                }
            }
        }

        return latest;

        void ObserveDispositionTimes(IReadOnlyList<ScheduleOccurrenceDispositionEvidence>? evidence)
        {
            if (evidence is null)
            {
                return;
            }

            for (var index = 0; index < Math.Min(evidence.Count, ScheduleContractLimits.MaxDispositionEvidenceItems + 1); index++)
            {
                if (evidence[index] is not null)
                {
                    Observe(evidence[index].RecordedAtUtc);
                }
            }
        }
    }

    internal static string Errors(ScheduleContractValidationResult validation)
        => string.Join(',', validation.Errors.Select(error => $"{error.Path}:{error.Code}"));
}
