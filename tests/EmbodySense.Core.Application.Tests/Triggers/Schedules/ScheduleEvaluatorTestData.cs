using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Tests.Triggers.Schedules;

internal static class ScheduleEvaluatorTestData
{
    internal static readonly byte[] Payload = [1, 2, 3, 4];
    internal static readonly DateTime FirstLocal = new(2026, 8, 12, 9, 30, 0, DateTimeKind.Unspecified);
    internal static readonly DateTimeOffset FirstUtc = new(2026, 8, 12, 14, 30, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset Now = FirstUtc.AddHours(1);
    internal const string RulesFingerprint = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    internal static ScheduleDefinition Definition(
        ScheduleRecurrenceKind recurrence = ScheduleRecurrenceKind.Daily,
        DateTime? firstLocal = null,
        long? intervalSeconds = null,
        ScheduleMisfirePolicyKind misfire = ScheduleMisfirePolicyKind.CatchUp,
        int catchUpLimit = 3,
        ScheduleInvalidLocalTimePolicy invalidLocal = ScheduleInvalidLocalTimePolicy.ShiftForward,
        ScheduleAmbiguousLocalTimePolicy ambiguousLocal = ScheduleAmbiguousLocalTimePolicy.EarlierUtc,
        ScheduleOverlapPolicy overlap = ScheduleOverlapPolicy.DeferOne,
        bool enabled = true)
    {
        Assert.True(ScheduleId.TryParse("daily-reflection", out var scheduleId));
        var target = TriggerAdmissionTestData.GovernedLoop(graphId: "daily-reflection");
        var adapter = TriggerAdmissionTestData.Adapter(implementation: "triggers/time");
        var actor = TriggerAdmissionTestData.ActorContext(surface: "scheduler");
        var authority = TriggerAdmissionTestData.Authority(evaluatedAtUtc: Now);
        return new ScheduleDefinition(
            1,
            scheduleId!,
            1,
            target,
            adapter,
            actor.ActorId,
            actor.SurfaceId,
            actor.WorkspaceId,
            actor.RoleId,
            authority.Profile,
            new SchedulePayloadReference("payload/daily-reflection", CapabilityIntegrityDigest.Compute(Payload)),
            SchedulePriority.Normal,
            new ScheduleRecurrenceRule(recurrence, firstLocal ?? FirstLocal, intervalSeconds),
            new ScheduleTimeZoneReference("America/Chicago", RulesFingerprint),
            new ScheduleDaylightSavingPolicy(invalidLocal, ambiguousLocal),
            new ScheduleMisfirePolicy(misfire, misfire == ScheduleMisfirePolicyKind.CatchUp ? catchUpLimit : 0),
            overlap,
            enabled);
    }

    internal static ScheduleOccurrence Occurrence(
        long ordinal = 1,
        DateTime? local = null,
        DateTimeOffset? utc = null,
        ScheduleTimeZoneReference? timeZone = null)
        => new(1, ordinal, local ?? FirstLocal, utc ?? FirstUtc, timeZone ?? new ScheduleTimeZoneReference("America/Chicago", RulesFingerprint));

    internal static ScheduleState State(
        ScheduleDefinition definition,
        ScheduleOccurrence? next = null,
        SchedulePendingDelivery? pending = null,
        bool? enabled = null,
        long revision = 1,
        DateTimeOffset? lastClock = null,
        ScheduleCatchUpEpisode? catchUp = null,
        ScheduleDeferredOccurrence? deferred = null,
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence>? dispositions = null,
        IReadOnlyList<ScheduleTerminalDeliveryEvidence>? terminal = null)
    {
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var hash, out var validation), Errors(validation));
        var occurrence = next ?? Occurrence(local: definition.Recurrence.FirstLocalOccurrence, timeZone: definition.TimeZone);
        return new ScheduleState(
            1,
            definition.ScheduleId,
            definition.Revision,
            hash!,
            revision,
            enabled ?? definition.Enabled,
            occurrence,
            catchUp,
            deferred,
            lastClock ?? occurrence.ScheduledAtUtc,
            pending,
            dispositions ?? [],
            terminal ?? []);
    }

    internal static ScheduleCurrentEvidence Evidence(
        ScheduleDefinition definition,
        DateTimeOffset observedAtUtc,
        string? evidenceHash = null,
        DateTimeOffset? authorityEvaluatedAtUtc = null)
        => new(
            evidenceHash ?? new string('f', 64),
            observedAtUtc,
            definition.Target,
            definition.TimeAdapter,
            TriggerAdmissionTestData.ActorContext(
                actor: definition.ActorId.Value,
                surface: definition.SurfaceId,
                workspace: definition.WorkspaceId,
                role: definition.RoleId),
            TriggerAdmissionTestData.Authority(evaluatedAtUtc: authorityEvaluatedAtUtc ?? observedAtUtc),
            true,
            Payload);

    internal static string Errors(ScheduleContractValidationResult validation)
        => string.Join(',', validation.Errors.Select(error => $"{error.Path}:{error.Code}"));
}

internal sealed class TestScheduleStore(
    ScheduleDefinition definition,
    ScheduleState state) : IScheduleStorePort
{
    internal ScheduleDefinition Definition { get; } = definition;
    internal ScheduleState State { get; set; } = state;
    internal ScheduleStoreReadStatus ReadStatus { get; set; } = ScheduleStoreReadStatus.Found;
    internal ScheduleStoreMutationStatus? NextMutationStatus { get; set; }
    internal bool ReturnNullRead { get; set; }
    internal bool ReturnNullMutation { get; set; }
    internal bool ReturnAppliedWithoutCurrentState { get; set; }
    internal bool ReturnNextMutationWithoutCurrentState { get; set; }
    internal bool ReturnExactReplay { get; set; }
    internal bool ThrowOnRead { get; set; }
    internal bool ThrowOnMutation { get; set; }
    internal bool CancelOnRead { get; set; }
    internal bool CancelOnMutation { get; set; }
    internal CancellationTokenSource? CancellationSource { get; set; }
    internal CancellationToken LastReadCancellationToken { get; private set; }
    internal CancellationToken LastMutationCancellationToken { get; private set; }
    internal Func<int, ScheduleStoreMutationStatus?>? MutationStatusSelector { get; set; }
    internal List<ScheduleStateCompareExchange> Mutations { get; } = [];

    public Task<ScheduleStoreReadResult> ReadAsync(ScheduleId scheduleId, CancellationToken cancellationToken = default)
    {
        LastReadCancellationToken = cancellationToken;
        return CancelOnRead
            ? Cancel<ScheduleStoreReadResult>()
            : ThrowOnRead
            ? throw new IOException("schedule store unavailable")
            : ReturnNullRead
            ? Task.FromResult<ScheduleStoreReadResult>(null!)
            : Task.FromResult(new ScheduleStoreReadResult(
                ReadStatus,
                ReadStatus == ScheduleStoreReadStatus.Found ? ScheduleContractCopy.Copy(Definition) : null,
                ReadStatus == ScheduleStoreReadStatus.Found ? ScheduleContractCopy.Copy(State) : null));
    }

    public Task<ScheduleStoreMutationResult> CreateAsync(ScheduleStoreCreateRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new ScheduleStoreMutationResult(ScheduleStoreMutationStatus.AlreadyExists, ScheduleContractCopy.Copy(State)));

    public Task<ScheduleStoreMutationResult> CompareExchangeAsync(ScheduleStateCompareExchange request, CancellationToken cancellationToken = default)
    {
        LastMutationCancellationToken = cancellationToken;
        Mutations.Add(request);
        if (CancelOnMutation)
        {
            return Cancel<ScheduleStoreMutationResult>();
        }

        if (ThrowOnMutation)
        {
            throw new IOException("schedule store unavailable");
        }

        if (ReturnNullMutation)
        {
            ReturnNullMutation = false;
            return Task.FromResult<ScheduleStoreMutationResult>(null!);
        }

        if (ReturnAppliedWithoutCurrentState)
        {
            ReturnAppliedWithoutCurrentState = false;
            return Task.FromResult(new ScheduleStoreMutationResult(ScheduleStoreMutationStatus.Applied, null));
        }

        if (ReturnExactReplay)
        {
            ReturnExactReplay = false;
            State = ScheduleContractCopy.Copy(request.Replacement)!;
            return Task.FromResult(new ScheduleStoreMutationResult(ScheduleStoreMutationStatus.Applied, ScheduleContractCopy.Copy(State)) { ExactReplay = true });
        }

        var selectedStatus = MutationStatusSelector?.Invoke(Mutations.Count) ?? NextMutationStatus;
        if (selectedStatus is { } forced)
        {
            NextMutationStatus = null;
            var current = ReturnNextMutationWithoutCurrentState ? null : ScheduleContractCopy.Copy(State);
            ReturnNextMutationWithoutCurrentState = false;
            return Task.FromResult(new ScheduleStoreMutationResult(forced, current));
        }

        if (!SameState(State, request.Expected))
        {
            return Task.FromResult(new ScheduleStoreMutationResult(ScheduleStoreMutationStatus.Conflict, ScheduleContractCopy.Copy(State)));
        }

        State = ScheduleContractCopy.Copy(request.Replacement)!;
        return Task.FromResult(new ScheduleStoreMutationResult(ScheduleStoreMutationStatus.Applied, ScheduleContractCopy.Copy(State)));
    }

    private static bool SameState(ScheduleState left, ScheduleState right)
        => ScheduleContractHash.TryComputeState(left, out var leftHash, out _)
            && ScheduleContractHash.TryComputeState(right, out var rightHash, out _)
            && string.Equals(leftHash, rightHash, StringComparison.Ordinal);

    private Task<T> Cancel<T>()
    {
        CancellationSource!.Cancel();
        return Task.FromCanceled<T>(CancellationSource.Token);
    }
}

internal sealed class TestScheduleCurrentEvidence : IScheduleCurrentEvidencePort
{
    internal ScheduleCurrentEvidenceStatus Status { get; set; } = ScheduleCurrentEvidenceStatus.Available;
    internal string EvidenceHash { get; set; } = new('f', 64);
    internal TimeSpan ObservationDelay { get; set; }
    internal TimeSpan AuthorityLead { get; set; }
    internal bool Throw { get; set; }
    internal bool Cancel { get; set; }
    internal CancellationTokenSource? CancellationSource { get; set; }
    internal CancellationToken LastCancellationToken { get; private set; }
    internal Func<ScheduleCurrentEvidence, ScheduleCurrentEvidence>? EvidenceMutation { get; set; }
    internal int Calls { get; private set; }

    public Task<ScheduleCurrentEvidenceResult> ResolveAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence occurrence,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        LastCancellationToken = cancellationToken;
        Calls++;
        if (Cancel)
        {
            CancellationSource!.Cancel();
            return Task.FromCanceled<ScheduleCurrentEvidenceResult>(CancellationSource.Token);
        }

        if (Throw)
        {
            throw new IOException("current evidence unavailable");
        }

        var resolvedAtUtc = observedAtUtc + ObservationDelay;
        var evidence = ScheduleEvaluatorTestData.Evidence(
            definition,
            resolvedAtUtc,
            EvidenceHash,
            resolvedAtUtc + AuthorityLead);
        return Task.FromResult(new ScheduleCurrentEvidenceResult(
            Status,
            Status == ScheduleCurrentEvidenceStatus.Available
                ? EvidenceMutation?.Invoke(evidence) ?? evidence
                : null));
    }
}

internal sealed class TestScheduleOverlap : IScheduleOverlapPort
{
    internal ScheduleOverlapStatus Status { get; set; } = ScheduleOverlapStatus.Clear;
    internal bool Throw { get; set; }
    internal bool Cancel { get; set; }
    internal CancellationTokenSource? CancellationSource { get; set; }
    internal string? EvidenceHashOverride { get; set; }
    internal CancellationToken LastCancellationToken { get; private set; }
    internal int Calls { get; private set; }

    public Task<ScheduleOverlapResult> GetStatusAsync(
        EmbodySense.Core.Common.Triggers.Models.TriggerLoopReference target,
        ScheduleOccurrenceIdentity occurrenceIdentity,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        LastCancellationToken = cancellationToken;
        Calls++;
        if (Cancel)
        {
            CancellationSource!.Cancel();
            return Task.FromCanceled<ScheduleOverlapResult>(CancellationSource.Token);
        }

        if (Throw)
        {
            throw new IOException("overlap evidence unavailable");
        }

        return Task.FromResult(new ScheduleOverlapResult(
            Status,
            EvidenceHashOverride
                ?? (Status is ScheduleOverlapStatus.Clear or ScheduleOverlapStatus.Active ? new string('a', 64) : null)));
    }
}

internal sealed class TestScheduleTimeZone : IScheduleTimeZonePort
{
    internal Func<ScheduleTimeZoneReference, DateTime, ScheduleTimeZoneResolution>? LocalResolver { get; set; }
    internal Func<ScheduleTimeZoneReference, DateTimeOffset, ScheduleInstantResolution>? InstantResolver { get; set; }
    internal bool CancelOnLocalResolution { get; set; }
    internal bool CancelOnInstantResolution { get; set; }
    internal int? CancelOnLocalCall { get; set; }
    internal int? CancelOnInstantCall { get; set; }
    internal int? ReturnNullLocalCall { get; set; }
    internal bool ReturnNullInstant { get; set; }
    internal bool ThrowOnInstantResolution { get; set; }
    internal CancellationTokenSource? CancellationSource { get; set; }
    internal int LocalCalls { get; private set; }
    internal int InstantCalls { get; private set; }
    internal List<DateTime> LocalRequests { get; } = [];
    internal List<DateTimeOffset> InstantRequests { get; } = [];
    internal CancellationToken LastLocalCancellationToken { get; private set; }
    internal CancellationToken LastInstantCancellationToken { get; private set; }

    public Task<ScheduleTimeZoneResolution> ResolveLocalAsync(
        ScheduleTimeZoneReference timeZone,
        DateTime scheduledLocal,
        CancellationToken cancellationToken = default)
    {
        LastLocalCancellationToken = cancellationToken;
        LocalCalls++;
        LocalRequests.Add(scheduledLocal);
        if (CancelOnLocalResolution || CancelOnLocalCall == LocalCalls)
        {
            CancellationSource!.Cancel();
            return Task.FromCanceled<ScheduleTimeZoneResolution>(CancellationSource.Token);
        }

        if (ReturnNullLocalCall == LocalCalls)
        {
            return Task.FromResult<ScheduleTimeZoneResolution>(null!);
        }

        var result = LocalResolver?.Invoke(timeZone, scheduledLocal)
            ?? new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                timeZone.RulesFingerprint,
                scheduledLocal,
                new DateTimeOffset(scheduledLocal.AddHours(5), TimeSpan.Zero),
                null);
        return Task.FromResult(result);
    }

    public Task<ScheduleInstantResolution> ResolveInstantAsync(
        ScheduleTimeZoneReference timeZone,
        DateTimeOffset scheduledAtUtc,
        CancellationToken cancellationToken = default)
    {
        LastInstantCancellationToken = cancellationToken;
        InstantCalls++;
        InstantRequests.Add(scheduledAtUtc);
        if (CancelOnInstantResolution || CancelOnInstantCall == InstantCalls)
        {
            CancellationSource!.Cancel();
            return Task.FromCanceled<ScheduleInstantResolution>(CancellationSource.Token);
        }

        if (ThrowOnInstantResolution)
        {
            throw new IOException("instant time-zone unavailable");
        }

        if (ReturnNullInstant)
        {
            return Task.FromResult<ScheduleInstantResolution>(null!);
        }

        var local = DateTime.SpecifyKind(scheduledAtUtc.UtcDateTime.AddHours(-5), DateTimeKind.Unspecified);
        return Task.FromResult(InstantResolver?.Invoke(timeZone, scheduledAtUtc)
            ?? new ScheduleInstantResolution(
                ScheduleInstantResolutionStatus.Resolved,
                timeZone.RulesFingerprint,
                local));
    }
}

internal sealed class TestScheduleQueue : ITriggerQueueAdmissionPort
{
    internal TriggerQueueAdmissionStatus Status { get; set; } = TriggerQueueAdmissionStatus.Queued;
    internal bool Throw { get; set; }
    internal bool Cancel { get; set; }
    internal CancellationTokenSource? CancellationSource { get; set; }
    internal CancellationToken LastCancellationToken { get; private set; }
    internal int Calls { get; private set; }
    internal List<TriggerQueueAdmissionRequest> Requests { get; } = [];
    internal TriggerQueueAdmissionReason? ReasonOverride { get; set; }
    internal TriggerAdmissionStatus? AdmissionStatusOverride { get; set; }
    internal TriggerAdmissionReason? AdmissionReasonOverride { get; set; }
    internal bool SubstituteDeliveryIdentity { get; set; }
    internal bool SubstituteDeduplicationIdentity { get; set; }

    public Task<TriggerQueueAdmissionResult> AdmitAsync(
        TriggerQueueAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        LastCancellationToken = cancellationToken;
        Calls++;
        Requests.Add(request);
        if (Cancel)
        {
            CancellationSource!.Cancel();
            return Task.FromCanceled<TriggerQueueAdmissionResult>(CancellationSource.Token);
        }

        if (Throw)
        {
            throw new InvalidOperationException("ambiguous queue failure");
        }

        var envelope = request.DeliveryRequest.Envelope;
        Assert.True(TriggerDeliveryHash.TryCompute(envelope, out var hash, out _));
        var reason = ReasonOverride ?? Status switch
        {
            TriggerQueueAdmissionStatus.Queued => TriggerQueueAdmissionReason.Enqueued,
            TriggerQueueAdmissionStatus.Replayed => TriggerQueueAdmissionReason.ExactReplay,
            TriggerQueueAdmissionStatus.Rejected => TriggerQueueAdmissionReason.AdmissionRejected,
            TriggerQueueAdmissionStatus.Backpressured => TriggerQueueAdmissionReason.QueueCountExceeded,
            _ => TriggerQueueAdmissionReason.StorageUnavailable,
        };
        var admissionStatus = AdmissionStatusOverride ?? Status switch
        {
            TriggerQueueAdmissionStatus.Replayed => TriggerAdmissionStatus.Replayed,
            TriggerQueueAdmissionStatus.Rejected => TriggerAdmissionStatus.Invalid,
            _ => TriggerAdmissionStatus.Admitted,
        };
        var admissionReason = AdmissionReasonOverride ?? admissionStatus switch
        {
            TriggerAdmissionStatus.Replayed => TriggerAdmissionReason.ExactReplay,
            TriggerAdmissionStatus.Invalid => TriggerAdmissionReason.InvalidEnvelope,
            _ => TriggerAdmissionReason.EvidenceAccepted,
        };
        var deliveryId = envelope.DeliveryId;
        var deduplicationId = envelope.DeduplicationId;
        if (SubstituteDeliveryIdentity)
        {
            Assert.True(TriggerDeliveryId.TryParse("trigger-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", out deliveryId));
        }

        if (SubstituteDeduplicationIdentity)
        {
            Assert.True(TriggerDeduplicationId.TryParse("trigger-dedup-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", out deduplicationId));
        }

        var entry = Status == TriggerQueueAdmissionStatus.Replayed
            ? QueuedEntry(
                envelope,
                hash!,
                request.Priority,
                request.DeliveryRequest.EvaluatedAtUtc,
                admissionStatus,
                admissionReason)
            : null;
        return Task.FromResult(new TriggerQueueAdmissionResult(
            Status,
            reason,
            deliveryId!,
            deduplicationId!,
            hash,
            entry,
            admissionStatus,
            admissionReason));
    }

    private static TriggerQueueEntry QueuedEntry(
        TriggerDeliveryEnvelope envelope,
        string canonicalEnvelopeHash,
        TriggerQueuePriority priority,
        DateTimeOffset recordedAtUtc,
        TriggerAdmissionStatus admissionStatus,
        TriggerAdmissionReason admissionReason)
        => new(
            envelope.DeliveryId,
            envelope.DeduplicationId,
            envelope.Loop.LoopId,
            canonicalEnvelopeHash,
            1,
            1,
            1,
            TriggerQueueEntryState.Queued,
            TriggerQueueTerminalReason.None,
            new TriggerQueueOrderKey(recordedAtUtc, priority, recordedAtUtc, envelope.DeliveryId.Value),
            1,
            recordedAtUtc,
            null,
            admissionStatus,
            admissionReason);
}

internal sealed class TestScheduleAdmissionHistory(
    params TriggerDeliveryAdmissionHistoryEntry?[] entries) : ITriggerDeliveryAdmissionHistoryPort
{
    public Task<TriggerDeliveryAdmissionHistoryLookupResult> FindAsync(
        TriggerDeliveryId deliveryId,
        TriggerDeduplicationId deduplicationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deliveryMatch = entries.SingleOrDefault(entry => entry is not null && entry.Envelope.DeliveryId.Equals(deliveryId));
        var deduplicationMatch = entries.SingleOrDefault(entry => entry is not null && entry.Envelope.DeduplicationId.Equals(deduplicationId));
        return Task.FromResult(new TriggerDeliveryAdmissionHistoryLookupResult(
            TriggerDeliveryAdmissionHistoryLookupStatus.Available,
            deliveryMatch,
            deduplicationMatch));
    }
}

internal sealed class TestScheduleQueueMutation : ITriggerQueueMutationPort
{
    internal List<TriggerQueueCommitRequest> Requests { get; } = [];

    public Task<TriggerQueueAdmissionResult> CommitAsync(
        TriggerQueueCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        var status = request.AdmissionStatus switch
        {
            TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.NotYetEligible
                => TriggerQueueAdmissionStatus.Queued,
            TriggerAdmissionStatus.Replayed => TriggerQueueAdmissionStatus.Replayed,
            _ => TriggerQueueAdmissionStatus.Rejected,
        };
        var reason = status switch
        {
            TriggerQueueAdmissionStatus.Queued => TriggerQueueAdmissionReason.Enqueued,
            TriggerQueueAdmissionStatus.Replayed => TriggerQueueAdmissionReason.ExactReplay,
            _ => TriggerQueueAdmissionReason.AdmissionRejected,
        };
        var entry = status == TriggerQueueAdmissionStatus.Replayed
            ? new TriggerQueueEntry(
                request.Envelope.DeliveryId,
                request.Envelope.DeduplicationId,
                request.Envelope.Loop.LoopId,
                request.CanonicalEnvelopeHash,
                1,
                1,
                1,
                TriggerQueueEntryState.Queued,
                TriggerQueueTerminalReason.None,
                new TriggerQueueOrderKey(
                    request.RecordedAtUtc,
                    request.Priority,
                    request.RecordedAtUtc,
                    request.Envelope.DeliveryId.Value),
                1,
                request.RecordedAtUtc,
                null,
                request.AdmissionStatus,
                request.AdmissionReason)
            : null;
        return Task.FromResult(new TriggerQueueAdmissionResult(
            status,
            reason,
            request.Envelope.DeliveryId,
            request.Envelope.DeduplicationId,
            request.CanonicalEnvelopeHash,
            entry,
            request.AdmissionStatus,
            request.AdmissionReason));
    }
}

internal sealed class TestScheduleTimeProvider(DateTimeOffset now) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = now;
    internal bool Throw { get; set; }

    public override DateTimeOffset GetUtcNow()
        => Throw ? throw new InvalidOperationException("schedule clock unavailable") : Now;
}
