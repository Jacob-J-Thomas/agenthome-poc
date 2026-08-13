using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Triggers.Schedules;

/// <summary>Evaluates and durably advances at most one exact schedule occurrence.</summary>
public sealed class ScheduleDueOccurrenceEvaluator
{
    private const int MaxRecurrenceProbes = ScheduleContractLimits.MaxFinalizationEvidenceItems + 1;
    private readonly IScheduleStorePort _store;
    private readonly IScheduleCurrentEvidencePort _currentEvidence;
    private readonly IScheduleOverlapPort _overlap;
    private readonly IScheduleTimeZonePort _timeZone;
    private readonly ITriggerQueueAdmissionPort _queue;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the one-shot evaluator over composition-owned ports.</summary>
    public ScheduleDueOccurrenceEvaluator(
        IScheduleStorePort store,
        IScheduleCurrentEvidencePort currentEvidence,
        IScheduleOverlapPort overlap,
        IScheduleTimeZonePort timeZone,
        ITriggerQueueAdmissionPort queue,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _currentEvidence = currentEvidence ?? throw new ArgumentNullException(nameof(currentEvidence));
        _overlap = overlap ?? throw new ArgumentNullException(nameof(overlap));
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Runs one due occurrence through durable claim, preparation, admission, and finalization.</summary>
    public async Task<ScheduleEvaluationResult> EvaluateAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheduleId);
        cancellationToken.ThrowIfCancellationRequested();

        ScheduleStoreReadResult? read;
        try
        {
            read = await _store.ReadAsync(scheduleId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(ScheduleEvaluationStatus.Unavailable, "schedule-store-unavailable", null);
        }

        if (!IsValidReadResult(scheduleId, read))
        {
            return Result(ScheduleEvaluationStatus.Corrupt, "schedule-store-evidence-invalid", null);
        }

        if (read.Status == ScheduleStoreReadStatus.NotFound)
        {
            return Result(ScheduleEvaluationStatus.NotFound, "schedule-not-found", null);
        }

        if (read.Status != ScheduleStoreReadStatus.Found || read.Definition is null || read.State is null)
        {
            return Result(
                ReadFailureStatus(read.Status),
                ReadFailureReason(read.Status),
                read.State);
        }

        var definition = read.Definition;
        var state = read.State;
        if (!ScheduleContractValidator.ValidateDefinitionStateComposition(definition, state).IsValid)
        {
            return Result(ScheduleEvaluationStatus.Corrupt, "definition-state-invalid", state);
        }

        DateTimeOffset now;
        try
        {
            now = _timeProvider.GetUtcNow();
        }
        catch
        {
            return Result(ScheduleEvaluationStatus.Unavailable, "schedule-clock-unavailable", state);
        }

        if (!IsUtc(now))
        {
            return Result(ScheduleEvaluationStatus.Corrupt, "schedule-clock-invalid", state);
        }

        if (state.LastClockObservedAtUtc is { } lastObserved && now < lastObserved)
        {
            return Result(ScheduleEvaluationStatus.ClockRollback, "schedule-clock-rollback", state);
        }

        if (state.PendingDelivery is null)
        {
            if (!definition.Enabled || !state.Enabled)
            {
                return Result(ScheduleEvaluationStatus.Disabled, "schedule-disabled", state);
            }

            if (state.NextOccurrence is null)
            {
                return Result(ScheduleEvaluationStatus.Exhausted, "schedule-exhausted", state);
            }

            if (state.NextOccurrence.ScheduledAtUtc > now)
            {
                return await ObserveNotDueAsync(definition, state, now, cancellationToken).ConfigureAwait(false);
            }

            var claimed = await ClaimAsync(definition, state, now, cancellationToken).ConfigureAwait(false);
            if (claimed.Status != ScheduleEvaluationStatus.Unknown || claimed.State?.PendingDelivery is null)
            {
                return claimed;
            }

            state = claimed.State;
        }

        if (state.LastClockObservedAtUtc != now)
        {
            var observed = await ObservePendingClockAsync(
                definition,
                state,
                now,
                cancellationToken).ConfigureAwait(false);
            if (observed.Status != ScheduleEvaluationStatus.Unknown || observed.State?.PendingDelivery is null)
            {
                return observed;
            }

            state = observed.State;
        }

        return state.PendingDelivery!.Phase switch
        {
            SchedulePendingDeliveryPhase.Claimed
                => await PrepareClaimedAsync(definition, state, now, cancellationToken).ConfigureAwait(false),
            SchedulePendingDeliveryPhase.Prepared
                => await SubmitPreparedAsync(definition, state, now, cancellationToken).ConfigureAwait(false),
            SchedulePendingDeliveryPhase.ResultObserved
                => await RecoverObservedAsync(definition, state, now, cancellationToken).ConfigureAwait(false),
            _ => Result(ScheduleEvaluationStatus.Corrupt, "pending-phase-invalid", state),
        };
    }

    private async Task<ScheduleEvaluationResult> ObservePendingClockAsync(
        ScheduleDefinition definition,
        ScheduleState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!TryNextRevision(state, out var revision))
        {
            return Result(ScheduleEvaluationStatus.BoundExceeded, "state-revision-exhausted", state);
        }

        var replacement = state with
        {
            StateRevision = revision,
            LastClockObservedAtUtc = now,
        };
        return await PersistAsync(
            definition,
            state,
            replacement,
            ScheduleEvaluationStatus.Unknown,
            "pending-clock-observed",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScheduleEvaluationResult> ObserveNotDueAsync(
        ScheduleDefinition definition,
        ScheduleState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (state.LastClockObservedAtUtc == now)
        {
            return Result(ScheduleEvaluationStatus.NotDue, "occurrence-not-due", state);
        }

        if (!TryNextRevision(state, out var revision))
        {
            return Result(ScheduleEvaluationStatus.BoundExceeded, "state-revision-exhausted", state);
        }

        var replacement = state with
        {
            StateRevision = revision,
            LastClockObservedAtUtc = now,
        };
        return await PersistAsync(
            definition,
            state,
            replacement,
            ScheduleEvaluationStatus.NotDue,
            "occurrence-not-due",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScheduleEvaluationResult> ClaimAsync(
        ScheduleDefinition definition,
        ScheduleState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var occurrence = state.NextOccurrence!;
        if (!ScheduleIdentityDerivation.TryDerive(
                state.ScheduleId,
                state.DefinitionRevision,
                state.DefinitionHash,
                occurrence,
                out var identity,
                out _)
            || !ScheduleClaimId.TryParse(
                "claim-" + identity!.OccurrenceId.Value[ScheduleOccurrenceId.Prefix.Length..],
                out var claimId)
            || !TryNextRevision(state, out var revision))
        {
            return Result(ScheduleEvaluationStatus.BoundExceeded, "claim-coordinates-invalid", state);
        }

        var pending = new SchedulePendingDelivery(
            SchedulePendingDelivery.CurrentSchemaVersion,
            SchedulePendingDeliveryPhase.Claimed,
            occurrence,
            identity,
            claimId!,
            now,
            null,
            null,
            null,
            null,
            null,
            null);
        var replacement = state with
        {
            StateRevision = revision,
            LastClockObservedAtUtc = now,
            PendingDelivery = pending,
        };
        var persisted = await PersistAsync(
            definition,
            state,
            replacement,
            ScheduleEvaluationStatus.Unknown,
            "occurrence-claimed",
            cancellationToken).ConfigureAwait(false);
        return persisted;
    }

    private async Task<ScheduleEvaluationResult> PrepareClaimedAsync(
        ScheduleDefinition definition,
        ScheduleState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = state.PendingDelivery!;
        var current = pending.Occurrence;
        var currentResolution = await ResolveCurrentOccurrenceAsync(
            definition,
            current,
            cancellationToken).ConfigureAwait(false);
        if (currentResolution.Status != RecurrenceStatus.Resolved)
        {
            return Result(
                RecurrenceFailureStatus(currentResolution.Status),
                currentResolution.ReasonCode,
                state);
        }

        var nextStep = await ResolveNextAsync(definition, current, now, cancellationToken).ConfigureAwait(false);
        if (nextStep.Status != RecurrenceStatus.Resolved && nextStep.Status != RecurrenceStatus.Exhausted)
        {
            return Result(RecurrenceFailureStatus(nextStep.Status), nextStep.ReasonCode, state);
        }

        if (currentResolution.ProofHashes.Count + nextStep.ProofHashes.Count
            > ScheduleContractLimits.MaxFinalizationEvidenceItems + 1)
        {
            return Result(ScheduleEvaluationStatus.BoundExceeded, "recurrence-evidence-bound-exceeded", state);
        }

        nextStep = nextStep with
        {
            ProofHashes = currentResolution.ProofHashes.Concat(nextStep.ProofHashes).ToArray(),
        };

        var simplePlan = new ScheduleFinalizationPlan(
            ScheduleFinalizationPlan.CurrentSchemaVersion,
            nextStep.NextOccurrence,
            null,
            null,
            nextStep.Skips.Select(item => item.Evidence).ToArray());

        if (now - current.ScheduledAtUtc > TriggerDeliveryLimits.MaxTemporalHorizon)
        {
            var horizonPlan = simplePlan;
            if (state.CatchUpEpisode is not null)
            {
                var activePlan = await BuildActiveCatchUpPlanAsync(
                    definition,
                    state,
                    now,
                    nextStep,
                    cancellationToken).ConfigureAwait(false);
                if (activePlan.Status != RecurrenceStatus.Resolved || activePlan.Plan is null)
                {
                    return Result(
                        RecurrenceFailureStatus(activePlan.Status),
                        activePlan.ReasonCode,
                        state);
                }

                horizonPlan = activePlan.Plan;
            }

            return await SkipClaimAsync(
                definition,
                state,
                now,
                ScheduleOccurrenceDisposition.MisfireSkipped,
                "temporal-horizon-exceeded",
                horizonPlan,
                ScheduleEvaluationStatus.Skipped,
                cancellationToken).ConfigureAwait(false);
        }

        var anotherOccurrenceIsDue = nextStep.NextOccurrence is { } immediateNext
            && immediateNext.ScheduledAtUtc <= now;
        if (state.CatchUpEpisode is null
            && (definition.Misfire.Kind == ScheduleMisfirePolicyKind.Skip
                && current.ScheduledAtUtc < now
                || definition.Misfire.Kind == ScheduleMisfirePolicyKind.FireLatestOnce
                && anotherOccurrenceIsDue))
        {
            return await SkipClaimAsync(
                definition,
                state,
                now,
                ScheduleOccurrenceDisposition.MisfireSkipped,
                definition.Misfire.Kind == ScheduleMisfirePolicyKind.Skip
                    ? "misfire-policy-skip"
                    : "misfire-fire-latest",
                simplePlan,
                ScheduleEvaluationStatus.Skipped,
                cancellationToken).ConfigureAwait(false);
        }

        PlanBuild planBuild;
        if (state.CatchUpEpisode is not null)
        {
            planBuild = await BuildActiveCatchUpPlanAsync(
                definition,
                state,
                now,
                nextStep,
                cancellationToken).ConfigureAwait(false);
        }
        else if (anotherOccurrenceIsDue && definition.Misfire.Kind == ScheduleMisfirePolicyKind.CatchUp)
        {
            planBuild = await BuildInitialCatchUpPlanAsync(
                definition,
                current,
                now,
                nextStep,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            planBuild = new PlanBuild(
                RecurrenceStatus.Resolved,
                simplePlan,
                null,
                "recurrence-resolved",
                nextStep.ProofHashes);
        }

        if (planBuild.Status != RecurrenceStatus.Resolved || planBuild.Plan is null)
        {
            return Result(RecurrenceFailureStatus(planBuild.Status), planBuild.ReasonCode, state);
        }

        ScheduleOverlapResult? overlap;
        try
        {
            overlap = await _overlap.GetStatusAsync(
                definition.Target,
                pending.Identity,
                now,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(ScheduleEvaluationStatus.Unavailable, "overlap-evidence-unavailable", state);
        }

        if (overlap is null || !Enum.IsDefined(overlap.Status) || overlap.Status == ScheduleOverlapStatus.Unknown)
        {
            return Result(ScheduleEvaluationStatus.Corrupt, "overlap-evidence-invalid", state);
        }

        var overlapHasProof = IsSha256(overlap.EvidenceHash);
        if (overlap.Status is ScheduleOverlapStatus.Clear or ScheduleOverlapStatus.Active
                ? !overlapHasProof
                : overlap.EvidenceHash is not null)
        {
            return Result(ScheduleEvaluationStatus.Corrupt, "overlap-evidence-invalid", state);
        }

        if (overlap.Status == ScheduleOverlapStatus.Active)
        {
            if (definition.Overlap == ScheduleOverlapPolicy.Skip)
            {
                var skipPlan = state.CatchUpEpisode is null && planBuild.CurrentEpisode is not null
                    ? simplePlan
                    : planBuild.Plan;
                return await SkipClaimAsync(
                    definition,
                    state,
                    now,
                    ScheduleOccurrenceDisposition.OverlapSkipped,
                    "overlap-policy-skip",
                    skipPlan,
                    ScheduleEvaluationStatus.Skipped,
                    cancellationToken,
                    decisionEvidenceHash: overlap.EvidenceHash).ConfigureAwait(false);
            }

            if (definition.Overlap == ScheduleOverlapPolicy.DeferOne)
            {
                return await DeferClaimAsync(
                    definition,
                    state,
                    now,
                    overlap.EvidenceHash!,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else if (overlap.Status != ScheduleOverlapStatus.Clear)
        {
            return Result(
                OverlapFailureStatus(overlap.Status),
                OverlapFailureReason(overlap.Status),
                state);
        }

        var currentEvidence = await ResolveCurrentEvidenceAsync(
            definition,
            current,
            now,
            cancellationToken).ConfigureAwait(false);
        if (currentEvidence.Status != ScheduleCurrentEvidenceStatus.Available || currentEvidence.Evidence is null)
        {
            return Result(
                CurrentEvidenceFailureStatus(currentEvidence.Status),
                CurrentEvidenceFailureReason(currentEvidence.Status),
                state);
        }

        var preparationObservedAtUtc = currentEvidence.Evidence.ObservedAtUtc;

        var preparationState = state with
        {
            CatchUpEpisode = planBuild.CurrentEpisode ?? state.CatchUpEpisode,
            LastClockObservedAtUtc = preparationObservedAtUtc,
        };
        if (!TryCreatePrepared(
                definition,
                preparationState,
                pending,
                currentEvidence.Evidence,
                overlap.EvidenceHash!,
                planBuild.Plan,
                planBuild.ProofHashes,
                preparationObservedAtUtc,
                out var preparedPending))
        {
            return Result(ScheduleEvaluationStatus.Corrupt, "prepared-delivery-invalid", state);
        }

        if (!TryNextRevision(state, out var revision))
        {
            return Result(ScheduleEvaluationStatus.BoundExceeded, "state-revision-exhausted", state);
        }

        var replacement = state with
        {
            StateRevision = revision,
            LastClockObservedAtUtc = preparationObservedAtUtc,
            CatchUpEpisode = planBuild.CurrentEpisode ?? state.CatchUpEpisode,
            PendingDelivery = preparedPending,
        };
        var persisted = await PersistAsync(
            definition,
            state,
            replacement,
            ScheduleEvaluationStatus.Unknown,
            "delivery-prepared",
            cancellationToken).ConfigureAwait(false);
        if (persisted.Status != ScheduleEvaluationStatus.Unknown || persisted.State?.PendingDelivery?.Phase != SchedulePendingDeliveryPhase.Prepared)
        {
            return persisted;
        }

        return await SubmitPreparedAsync(
            definition,
            persisted.State,
            preparationObservedAtUtc,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScheduleEvaluationResult> SubmitPreparedAsync(
        ScheduleDefinition definition,
        ScheduleState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = state.PendingDelivery!;
        var prepared = pending.Prepared!;
        var current = await ResolveCurrentEvidenceAsync(
            definition,
            pending.Occurrence,
            now,
            cancellationToken).ConfigureAwait(false);
        if (current.Status != ScheduleCurrentEvidenceStatus.Available || current.Evidence is null)
        {
            return Result(
                CurrentEvidenceFailureStatus(current.Status),
                CurrentEvidenceFailureReason(current.Status),
                state);
        }
        var observedAtUtc = current.Evidence.ObservedAtUtc;

        if (!TriggerDeliveryAdmissionRequestFactory.TryCreatePreparedScheduleRecovery(
                prepared.Envelope,
                current.Evidence.Target,
                current.Evidence.Adapter,
                true,
                current.Evidence.ActorContext,
                current.Evidence.Authority,
                observedAtUtc,
                out var deliveryRequest,
                out _))
        {
            return Result(ScheduleEvaluationStatus.Corrupt, "current-evidence-invalid", state);
        }

        TriggerQueueAdmissionResult? queueResult;
        try
        {
            queueResult = await _queue.AdmitAsync(
                new TriggerQueueAdmissionRequest(
                    deliveryRequest!,
                    TriggerQueueAdmissionMode.Queued,
                    QueuePriority(definition.Priority)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await RecordAmbiguousAsync(
                definition,
                state,
                observedAtUtc,
                "queue-outcome-ambiguous",
                current.Evidence.EvidenceHash).ConfigureAwait(false);
        }

        if (!QueueResultMatches(queueResult, pending, prepared))
        {
            return await RecordAmbiguousAsync(
                definition,
                state,
                observedAtUtc,
                "queue-evidence-conflict",
                current.Evidence.EvidenceHash).ConfigureAwait(false);
        }

        var exactQueueResult = queueResult!;
        var deliveryKind = exactQueueResult.Status switch
        {
            TriggerQueueAdmissionStatus.Queued => ScheduleDeliveryResultKind.Queued,
            TriggerQueueAdmissionStatus.Replayed => ScheduleDeliveryResultKind.Replayed,
            TriggerQueueAdmissionStatus.Rejected => ScheduleDeliveryResultKind.Rejected,
            TriggerQueueAdmissionStatus.Backpressured => ScheduleDeliveryResultKind.Backpressured,
            TriggerQueueAdmissionStatus.Unavailable => ScheduleDeliveryResultKind.Ambiguous,
            _ => ScheduleDeliveryResultKind.Ambiguous,
        };
        var reason = exactQueueResult.Status switch
        {
            TriggerQueueAdmissionStatus.Queued => "queue-enqueued",
            TriggerQueueAdmissionStatus.Replayed => "queue-exact-replay",
            TriggerQueueAdmissionStatus.Rejected => "queue-admission-rejected",
            TriggerQueueAdmissionStatus.Backpressured => "queue-backpressured",
            _ => "queue-outcome-ambiguous",
        };
        var observed = new ScheduleDeliveryResultEvidence(
            ScheduleDeliveryResultEvidence.CurrentSchemaVersion,
            deliveryKind,
            reason,
            prepared.CanonicalEnvelopeHash,
            observedAtUtc);
        var observedPending = pending with
        {
            Phase = SchedulePendingDeliveryPhase.ResultObserved,
            CurrentEvidenceHash = current.Evidence.EvidenceHash,
            Result = observed,
        };
        if (!TryNextRevision(state, out var revision))
        {
            return Result(ScheduleEvaluationStatus.BoundExceeded, "state-revision-exhausted", state);
        }

        var replacement = state with
        {
            StateRevision = revision,
            LastClockObservedAtUtc = observedAtUtc,
            PendingDelivery = observedPending,
        };
        var persisted = await PersistAsync(
            definition,
            state,
            replacement,
            ScheduleEvaluationStatus.Unknown,
            "queue-result-observed",
            CancellationToken.None).ConfigureAwait(false);
        if (persisted.Status != ScheduleEvaluationStatus.Unknown || persisted.State is null)
        {
            return persisted;
        }

        if (deliveryKind == ScheduleDeliveryResultKind.Backpressured)
        {
            return Result(ScheduleEvaluationStatus.Backpressured, reason, persisted.State);
        }

        if (deliveryKind == ScheduleDeliveryResultKind.Ambiguous)
        {
            return Result(ScheduleEvaluationStatus.NeedsReview, reason, persisted.State);
        }

        return await FinalizeObservedAsync(
            definition,
            persisted.State,
            observedAtUtc,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<ScheduleEvaluationResult> RecoverObservedAsync(
        ScheduleDefinition definition,
        ScheduleState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var kind = state.PendingDelivery!.Result!.Kind;
        if (kind is ScheduleDeliveryResultKind.Queued or ScheduleDeliveryResultKind.Replayed or ScheduleDeliveryResultKind.Rejected)
        {
            return await FinalizeObservedAsync(definition, state, now, cancellationToken).ConfigureAwait(false);
        }

        if (kind == ScheduleDeliveryResultKind.Backpressured || kind == ScheduleDeliveryResultKind.Unavailable)
        {
            return await SubmitPreparedAsync(definition, state, now, cancellationToken).ConfigureAwait(false);
        }

        if (kind == ScheduleDeliveryResultKind.Ambiguous)
        {
            return Result(ScheduleEvaluationStatus.NeedsReview, "queue-outcome-ambiguous", state);
        }

        return Result(ScheduleEvaluationStatus.Corrupt, "queue-result-invalid", state);
    }

    private async Task<ScheduleEvaluationResult> FinalizeObservedAsync(
        ScheduleDefinition definition,
        ScheduleState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = state.PendingDelivery!;
        var result = pending.Result!;
        var plan = pending.FinalizationPlan!;
        if (state.TerminalDeliveryEvidence.Count >= ScheduleContractLimits.MaxTerminalDeliveryEvidenceItems
            || state.DispositionEvidence.Count + plan.DispositionEvidence.Count > ScheduleContractLimits.MaxDispositionEvidenceItems
            || !TryNextRevision(state, out var revision))
        {
            return Result(ScheduleEvaluationStatus.BoundExceeded, "schedule-evidence-bound-exceeded", state);
        }

        var terminal = new ScheduleTerminalDeliveryEvidence(
            ScheduleTerminalDeliveryEvidence.CurrentSchemaVersion,
            pending.Occurrence,
            pending.Identity,
            pending.CurrentEvidenceHash!,
            pending.RecurrenceProofHash!,
            pending.OverlapEvidenceHash!,
            result,
            now);
        var replacement = state with
        {
            StateRevision = revision,
            NextOccurrence = plan.NextOccurrence,
            CatchUpEpisode = plan.CatchUpEpisode,
            DeferredOccurrence = plan.DeferredOccurrence,
            LastClockObservedAtUtc = now,
            PendingDelivery = null,
            DispositionEvidence = state.DispositionEvidence.Concat(plan.DispositionEvidence).ToArray(),
            TerminalDeliveryEvidence = state.TerminalDeliveryEvidence.Append(terminal).ToArray(),
        };
        var status = result.Kind switch
        {
            ScheduleDeliveryResultKind.Queued => ScheduleEvaluationStatus.Queued,
            ScheduleDeliveryResultKind.Replayed => ScheduleEvaluationStatus.Replayed,
            ScheduleDeliveryResultKind.Rejected => ScheduleEvaluationStatus.Rejected,
            _ => ScheduleEvaluationStatus.Corrupt,
        };
        return await PersistAsync(
            definition,
            state,
            replacement,
            status,
            result.ReasonCode,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScheduleEvaluationResult> RecordAmbiguousAsync(
        ScheduleDefinition definition,
        ScheduleState state,
        DateTimeOffset now,
        string reasonCode,
        string currentEvidenceHash)
    {
        var pending = state.PendingDelivery!;
        if (!TryNextRevision(state, out var revision))
        {
            return Result(ScheduleEvaluationStatus.BoundExceeded, "state-revision-exhausted", state);
        }

        var observed = new ScheduleDeliveryResultEvidence(
            ScheduleDeliveryResultEvidence.CurrentSchemaVersion,
            ScheduleDeliveryResultKind.Ambiguous,
            reasonCode,
            pending.Prepared!.CanonicalEnvelopeHash,
            now);
        var replacement = state with
        {
            StateRevision = revision,
            LastClockObservedAtUtc = now,
            PendingDelivery = pending with
            {
                Phase = SchedulePendingDeliveryPhase.ResultObserved,
                CurrentEvidenceHash = currentEvidenceHash,
                Result = observed,
            },
        };
        return await PersistAsync(
            definition,
            state,
            replacement,
            ScheduleEvaluationStatus.NeedsReview,
            reasonCode,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<ScheduleEvaluationResult> SkipClaimAsync(
        ScheduleDefinition definition,
        ScheduleState state,
        DateTimeOffset now,
        ScheduleOccurrenceDisposition disposition,
        string reasonCode,
        ScheduleFinalizationPlan plan,
        ScheduleEvaluationStatus status,
        CancellationToken cancellationToken,
        string? decisionEvidenceHash = null,
        ScheduleState? expectedState = null)
    {
        var pending = state.PendingDelivery!;
        var evidence = Disposition(
            pending.Occurrence,
            disposition,
            reasonCode,
            now,
            decisionEvidenceHash);
        var planned = plan.DispositionEvidence;
        if (state.DispositionEvidence.Count + planned.Count + 1 > ScheduleContractLimits.MaxDispositionEvidenceItems
            || !TryNextRevision(expectedState ?? state, out var revision))
        {
            return Result(ScheduleEvaluationStatus.BoundExceeded, "schedule-evidence-bound-exceeded", expectedState ?? state);
        }

        var replacement = state with
        {
            StateRevision = revision,
            NextOccurrence = plan.NextOccurrence,
            CatchUpEpisode = plan.CatchUpEpisode,
            DeferredOccurrence = plan.DeferredOccurrence,
            LastClockObservedAtUtc = now,
            PendingDelivery = null,
            DispositionEvidence = state.DispositionEvidence
                .Append(evidence)
                .Concat(planned)
                .ToArray(),
        };
        return await PersistAsync(
            definition,
            expectedState ?? state,
            replacement,
            status,
            reasonCode,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScheduleEvaluationResult> DeferClaimAsync(
        ScheduleDefinition definition,
        ScheduleState state,
        DateTimeOffset now,
        string overlapEvidenceHash,
        CancellationToken cancellationToken)
    {
        var pending = state.PendingDelivery!;
        if (!TryNextRevision(state, out var revision))
        {
            return Result(ScheduleEvaluationStatus.BoundExceeded, "state-revision-exhausted", state);
        }

        ScheduleDeferredOccurrence deferred;
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence> evidence;
        if (state.DeferredOccurrence is not null)
        {
            deferred = state.DeferredOccurrence;
            evidence = state.DispositionEvidence;
        }
        else
        {
            if (state.DispositionEvidence.Count >= ScheduleContractLimits.MaxDispositionEvidenceItems)
            {
                return Result(ScheduleEvaluationStatus.BoundExceeded, "schedule-evidence-bound-exceeded", state);
            }

            deferred = new ScheduleDeferredOccurrence(
                ScheduleDeferredOccurrence.CurrentSchemaVersion,
                pending.Occurrence,
                pending.Identity,
                now);
            evidence = state.DispositionEvidence
                .Append(Disposition(
                    pending.Occurrence,
                    ScheduleOccurrenceDisposition.OverlapDeferred,
                    "overlap-policy-defer",
                    now,
                    overlapEvidenceHash))
                .ToArray();
        }

        var replacement = state with
        {
            StateRevision = revision,
            DeferredOccurrence = deferred,
            LastClockObservedAtUtc = now,
            PendingDelivery = null,
            DispositionEvidence = evidence,
        };
        return await PersistAsync(
            definition,
            state,
            replacement,
            ScheduleEvaluationStatus.Deferred,
            "overlap-policy-defer",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlanBuild> BuildInitialCatchUpPlanAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence current,
        DateTimeOffset now,
        RecurrenceStep firstStep,
        CancellationToken cancellationToken)
    {
        if (definition.Recurrence.Kind == ScheduleRecurrenceKind.FixedInterval)
        {
            return await BuildInitialFixedCatchUpPlanAsync(
                definition,
                current,
                now,
                firstStep,
                cancellationToken).ConfigureAwait(false);
        }

        var scan = await ScanDueAsync(definition, current, now, firstStep, cancellationToken).ConfigureAwait(false);
        if (scan.Status != RecurrenceStatus.Resolved)
        {
            return new PlanBuild(scan.Status, null, null, scan.ReasonCode, scan.ProofHashes);
        }

        var remaining = Math.Min(definition.Misfire.CatchUpLimit, scan.DueOccurrences.Count);
        var latestDueOrdinal = scan.LatestDueOrdinal;
        var currentEpisode = new ScheduleCatchUpEpisode(
            ScheduleCatchUpEpisode.CurrentSchemaVersion,
            latestDueOrdinal,
            remaining);
        if (remaining > 1)
        {
            var next = scan.DueOccurrences[1];
            var evidence = scan.Skips
                .Where(item => item.Evidence.LastOrdinal < next.Ordinal)
                .Select(item => item.Evidence)
                .ToArray();
            var successorEpisode = currentEpisode with
            {
                RemainingAdmittedOccurrences = remaining - 1,
            };
            return new PlanBuild(
                RecurrenceStatus.Resolved,
                new ScheduleFinalizationPlan(1, next, successorEpisode, null, evidence),
                currentEpisode,
                "catch-up-planned",
                scan.ProofHashes);
        }

        var dispositions = scan.Skips.Select(item => item.Evidence)
            .Concat(scan.DueOccurrences.Skip(1).Select(occurrence => Disposition(
                occurrence,
                ScheduleOccurrenceDisposition.MisfireSkipped,
                "catch-up-budget-exhausted",
                now)))
            .ToArray();
        if (dispositions.Length > ScheduleContractLimits.MaxFinalizationEvidenceItems)
        {
            return new PlanBuild(RecurrenceStatus.BoundExceeded, null, null, "recurrence-evidence-bound-exceeded", scan.ProofHashes);
        }

        return new PlanBuild(
            RecurrenceStatus.Resolved,
            new ScheduleFinalizationPlan(1, scan.FirstFutureOccurrence, null, null, dispositions),
            currentEpisode,
            "catch-up-planned",
            scan.ProofHashes);
    }

    private async Task<PlanBuild> BuildActiveCatchUpPlanAsync(
        ScheduleDefinition definition,
        ScheduleState state,
        DateTimeOffset now,
        RecurrenceStep firstStep,
        CancellationToken cancellationToken)
    {
        if (definition.Recurrence.Kind == ScheduleRecurrenceKind.FixedInterval)
        {
            return await BuildActiveFixedCatchUpPlanAsync(
                definition,
                state,
                now,
                firstStep,
                cancellationToken).ConfigureAwait(false);
        }

        var episode = state.CatchUpEpisode!;
        var scan = await ScanThroughOrdinalAsync(
            definition,
            state.PendingDelivery!.Occurrence,
            episode.LatestDueOrdinal,
            now,
            firstStep,
            cancellationToken).ConfigureAwait(false);
        if (scan.Status != RecurrenceStatus.Resolved)
        {
            return new PlanBuild(scan.Status, null, null, scan.ReasonCode, scan.ProofHashes);
        }

        if (episode.RemainingAdmittedOccurrences > 1)
        {
            var next = scan.DueOccurrences.Skip(1).FirstOrDefault();
            if (next is null)
            {
                return new PlanBuild(RecurrenceStatus.Corrupt, null, null, "catch-up-episode-invalid", scan.ProofHashes);
            }

            var evidence = scan.Skips
                .Where(item => item.Evidence.LastOrdinal < next.Ordinal)
                .Select(item => item.Evidence)
                .ToArray();
            var successor = episode with
            {
                RemainingAdmittedOccurrences = episode.RemainingAdmittedOccurrences - 1,
            };
            return new PlanBuild(
                RecurrenceStatus.Resolved,
                new ScheduleFinalizationPlan(1, next, successor, null, evidence),
                episode,
                "catch-up-planned",
                scan.ProofHashes);
        }

        var dispositions = scan.Skips.Select(item => item.Evidence)
            .Concat(scan.DueOccurrences.Skip(1).Select(occurrence => Disposition(
                occurrence,
                ScheduleOccurrenceDisposition.MisfireSkipped,
                "catch-up-budget-exhausted",
                now)))
            .ToArray();
        if (dispositions.Length > ScheduleContractLimits.MaxFinalizationEvidenceItems)
        {
            return new PlanBuild(RecurrenceStatus.BoundExceeded, null, null, "recurrence-evidence-bound-exceeded", scan.ProofHashes);
        }

        return new PlanBuild(
            RecurrenceStatus.Resolved,
            new ScheduleFinalizationPlan(1, scan.FirstFutureOccurrence, null, null, dispositions),
            episode,
            "catch-up-planned",
            scan.ProofHashes);
    }

    private async Task<PlanBuild> BuildInitialFixedCatchUpPlanAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence current,
        DateTimeOffset now,
        RecurrenceStep firstStep,
        CancellationToken cancellationToken)
    {
        if (firstStep.Status != RecurrenceStatus.Resolved || firstStep.NextOccurrence is null
            || !TryGetFixedLatestDueOrdinal(definition, current, now, out var latestDueOrdinal)
            || latestDueOrdinal <= current.Ordinal)
        {
            return new PlanBuild(
                firstStep.Status == RecurrenceStatus.Resolved ? RecurrenceStatus.Corrupt : firstStep.Status,
                null,
                null,
                firstStep.Status == RecurrenceStatus.Resolved ? "catch-up-episode-invalid" : firstStep.ReasonCode,
                firstStep.ProofHashes);
        }

        var dueCount = latestDueOrdinal - current.Ordinal + 1;
        var remaining = (int)Math.Min(definition.Misfire.CatchUpLimit, dueCount);
        var currentEpisode = new ScheduleCatchUpEpisode(
            ScheduleCatchUpEpisode.CurrentSchemaVersion,
            latestDueOrdinal,
            remaining);
        if (remaining > 1)
        {
            return new PlanBuild(
                RecurrenceStatus.Resolved,
                new ScheduleFinalizationPlan(
                    ScheduleFinalizationPlan.CurrentSchemaVersion,
                    firstStep.NextOccurrence,
                    currentEpisode with { RemainingAdmittedOccurrences = remaining - 1 },
                    null,
                    []),
                currentEpisode,
                "catch-up-planned",
                firstStep.ProofHashes);
        }

        return await BuildFixedCatchUpExhaustionPlanAsync(
            definition,
            current,
            latestDueOrdinal,
            currentEpisode,
            firstStep,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlanBuild> BuildActiveFixedCatchUpPlanAsync(
        ScheduleDefinition definition,
        ScheduleState state,
        DateTimeOffset now,
        RecurrenceStep firstStep,
        CancellationToken cancellationToken)
    {
        var episode = state.CatchUpEpisode!;
        var current = state.PendingDelivery!.Occurrence;
        if (episode.LatestDueOrdinal < current.Ordinal)
        {
            return new PlanBuild(
                RecurrenceStatus.Corrupt,
                null,
                null,
                "catch-up-episode-invalid",
                firstStep.ProofHashes);
        }

        if (episode.RemainingAdmittedOccurrences > 1)
        {
            if (firstStep.Status != RecurrenceStatus.Resolved
                || firstStep.NextOccurrence is null
                || firstStep.NextOccurrence.Ordinal > episode.LatestDueOrdinal)
            {
                return new PlanBuild(
                    RecurrenceStatus.Corrupt,
                    null,
                    null,
                    "catch-up-episode-invalid",
                    firstStep.ProofHashes);
            }

            return new PlanBuild(
                RecurrenceStatus.Resolved,
                new ScheduleFinalizationPlan(
                    ScheduleFinalizationPlan.CurrentSchemaVersion,
                    firstStep.NextOccurrence,
                    episode with
                    {
                        RemainingAdmittedOccurrences = episode.RemainingAdmittedOccurrences - 1,
                    },
                    null,
                    []),
                episode,
                "catch-up-planned",
                firstStep.ProofHashes);
        }

        return await BuildFixedCatchUpExhaustionPlanAsync(
            definition,
            current,
            episode.LatestDueOrdinal,
            episode,
            firstStep,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlanBuild> BuildFixedCatchUpExhaustionPlanAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence current,
        long latestDueOrdinal,
        ScheduleCatchUpEpisode currentEpisode,
        RecurrenceStep firstStep,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var proofHashes = firstStep.ProofHashes.ToList();
        ScheduleOccurrenceDispositionEvidence[] dispositions = [];
        if (current.Ordinal < latestDueOrdinal)
        {
            if (firstStep.Status != RecurrenceStatus.Resolved || firstStep.NextOccurrence is null)
            {
                return new PlanBuild(firstStep.Status, null, null, firstStep.ReasonCode, proofHashes);
            }

            var firstSkipped = firstStep.NextOccurrence;
            var lastStep = latestDueOrdinal == firstSkipped.Ordinal
                ? firstStep
                : await ResolveFixedOrdinalAsync(
                    definition,
                    current,
                    latestDueOrdinal,
                    cancellationToken).ConfigureAwait(false);
            if (lastStep.Status != RecurrenceStatus.Resolved || lastStep.NextOccurrence is null)
            {
                return new PlanBuild(lastStep.Status, null, null, lastStep.ReasonCode, proofHashes);
            }

            if (!ReferenceEquals(lastStep, firstStep))
            {
                proofHashes.AddRange(lastStep.ProofHashes);
            }

            var lastSkipped = lastStep.NextOccurrence;
            dispositions =
            [
                new ScheduleOccurrenceDispositionEvidence(
                    ScheduleOccurrenceDispositionEvidence.CurrentSchemaVersion,
                    firstSkipped.Ordinal,
                    lastSkipped.Ordinal,
                    lastSkipped.Ordinal - firstSkipped.Ordinal + 1,
                    firstSkipped.ScheduledLocal,
                    lastSkipped.ScheduledLocal,
                    firstSkipped.ScheduledAtUtc,
                    lastSkipped.ScheduledAtUtc,
                    definition.TimeZone,
                    ScheduleOccurrenceDisposition.MisfireSkipped,
                    null,
                    "catch-up-budget-exhausted",
                    now),
            ];
        }

        RecurrenceStep futureStep;
        if (current.Ordinal == latestDueOrdinal)
        {
            futureStep = firstStep;
        }
        else
        {
            futureStep = await ResolveFixedOrdinalAsync(
                definition,
                current,
                latestDueOrdinal + 1,
                cancellationToken).ConfigureAwait(false);
            proofHashes.AddRange(futureStep.ProofHashes);
        }

        if (futureStep.Status is not (RecurrenceStatus.Resolved or RecurrenceStatus.Exhausted)
            || futureStep.Status == RecurrenceStatus.Resolved && futureStep.NextOccurrence is null)
        {
            return new PlanBuild(futureStep.Status, null, null, futureStep.ReasonCode, proofHashes);
        }

        return new PlanBuild(
            RecurrenceStatus.Resolved,
            new ScheduleFinalizationPlan(
                ScheduleFinalizationPlan.CurrentSchemaVersion,
                futureStep.NextOccurrence,
                null,
                null,
                dispositions),
            currentEpisode,
            "catch-up-planned",
            proofHashes);
    }

    private static bool TryGetFixedLatestDueOrdinal(
        ScheduleDefinition definition,
        ScheduleOccurrence current,
        DateTimeOffset now,
        out long latestDueOrdinal)
    {
        latestDueOrdinal = current.Ordinal;
        var intervalTicks = definition.Recurrence.FixedIntervalSeconds!.Value * TimeSpan.TicksPerSecond;
        var currentTicks = current.ScheduledAtUtc.UtcDateTime.Ticks;
        var nowTicks = now.UtcDateTime.Ticks;
        if (nowTicks < currentTicks)
        {
            return false;
        }

        var deltaByTime = (nowTicks - currentTicks) / intervalTicks;
        var deltaByOrdinal = ScheduleContractLimits.MaxOccurrenceOrdinal - current.Ordinal;
        var deltaBySupportedTime = (MaximumSupportedTicks() - currentTicks) / intervalTicks;
        latestDueOrdinal = current.Ordinal + Math.Min(deltaByTime, Math.Min(deltaByOrdinal, deltaBySupportedTime));
        return true;
    }

    private async Task<RecurrenceScan> ScanDueAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence current,
        DateTimeOffset now,
        RecurrenceStep firstStep,
        CancellationToken cancellationToken)
    {
        var due = new List<ScheduleOccurrence> { current };
        var skips = new List<ResolvedSkip>();
        var proofHashes = new List<string>();
        var latestDueOrdinal = current.Ordinal;
        var cursor = current;
        var step = firstStep;
        for (var probe = 0; probe < MaxRecurrenceProbes; probe++)
        {
            proofHashes.AddRange(step.ProofHashes);
            skips.AddRange(step.Skips);
            if (skips.Count > ScheduleContractLimits.MaxFinalizationEvidenceItems)
            {
                return new RecurrenceScan(RecurrenceStatus.BoundExceeded, due, skips, null, latestDueOrdinal, "recurrence-evidence-bound-exceeded", proofHashes);
            }
            foreach (var skip in step.Skips)
            {
                if (skip.EffectiveAtUtc <= now)
                {
                    latestDueOrdinal = Math.Max(latestDueOrdinal, skip.Evidence.LastOrdinal);
                }
            }

            if (step.Status == RecurrenceStatus.Exhausted)
            {
                return proofHashes.Count > ScheduleContractLimits.MaxFinalizationEvidenceItems + 1
                    ? new RecurrenceScan(RecurrenceStatus.BoundExceeded, due, skips, null, latestDueOrdinal, "recurrence-evidence-bound-exceeded", proofHashes)
                    : new RecurrenceScan(RecurrenceStatus.Resolved, due, skips, null, latestDueOrdinal, "recurrence-exhausted", proofHashes);
            }

            if (step.Status != RecurrenceStatus.Resolved || step.NextOccurrence is null)
            {
                return new RecurrenceScan(step.Status, due, skips, null, latestDueOrdinal, step.ReasonCode, proofHashes);
            }

            if (step.NextOccurrence.ScheduledAtUtc > now)
            {
                return proofHashes.Count > ScheduleContractLimits.MaxFinalizationEvidenceItems + 1
                    ? new RecurrenceScan(RecurrenceStatus.BoundExceeded, due, skips, null, latestDueOrdinal, "recurrence-evidence-bound-exceeded", proofHashes)
                    : new RecurrenceScan(RecurrenceStatus.Resolved, due, skips, step.NextOccurrence, latestDueOrdinal, "recurrence-resolved", proofHashes);
            }

            cursor = step.NextOccurrence;
            due.Add(cursor);
            latestDueOrdinal = Math.Max(latestDueOrdinal, cursor.Ordinal);
            step = await ResolveNextAsync(definition, cursor, now, cancellationToken).ConfigureAwait(false);
        }

        return new RecurrenceScan(RecurrenceStatus.BoundExceeded, due, skips, null, latestDueOrdinal, "recurrence-probe-bound-exceeded", proofHashes);
    }

    private async Task<RecurrenceScan> ScanThroughOrdinalAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence current,
        long latestDueOrdinal,
        DateTimeOffset now,
        RecurrenceStep firstStep,
        CancellationToken cancellationToken)
    {
        var due = new List<ScheduleOccurrence> { current };
        var skips = new List<ResolvedSkip>();
        var proofHashes = new List<string>();
        var cursor = current;
        var step = firstStep;
        for (var probe = 0; probe < MaxRecurrenceProbes; probe++)
        {
            proofHashes.AddRange(step.ProofHashes);
            skips.AddRange(step.Skips);
            if (skips.Count > ScheduleContractLimits.MaxFinalizationEvidenceItems)
            {
                return new RecurrenceScan(RecurrenceStatus.BoundExceeded, due, skips, null, latestDueOrdinal, "recurrence-evidence-bound-exceeded", proofHashes);
            }
            if (step.Status == RecurrenceStatus.Exhausted)
            {
                return proofHashes.Count > ScheduleContractLimits.MaxFinalizationEvidenceItems + 1
                    ? new RecurrenceScan(RecurrenceStatus.BoundExceeded, due, skips, null, latestDueOrdinal, "recurrence-evidence-bound-exceeded", proofHashes)
                    : new RecurrenceScan(RecurrenceStatus.Resolved, due, skips, null, latestDueOrdinal, "recurrence-exhausted", proofHashes);
            }

            if (step.Status != RecurrenceStatus.Resolved || step.NextOccurrence is null)
            {
                return new RecurrenceScan(step.Status, due, skips, null, latestDueOrdinal, step.ReasonCode, proofHashes);
            }

            if (step.NextOccurrence.Ordinal > latestDueOrdinal)
            {
                return proofHashes.Count > ScheduleContractLimits.MaxFinalizationEvidenceItems + 1
                    ? new RecurrenceScan(RecurrenceStatus.BoundExceeded, due, skips, null, latestDueOrdinal, "recurrence-evidence-bound-exceeded", proofHashes)
                    : new RecurrenceScan(RecurrenceStatus.Resolved, due, skips, step.NextOccurrence, latestDueOrdinal, "recurrence-resolved", proofHashes);
            }

            cursor = step.NextOccurrence;
            due.Add(cursor);
            step = await ResolveNextAsync(definition, cursor, now, cancellationToken).ConfigureAwait(false);
        }

        return new RecurrenceScan(RecurrenceStatus.BoundExceeded, due, skips, null, latestDueOrdinal, "recurrence-probe-bound-exceeded", proofHashes);
    }

    private async Task<RecurrenceStep> ResolveCurrentOccurrenceAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence current,
        CancellationToken cancellationToken)
        => definition.Recurrence.Kind != ScheduleRecurrenceKind.FixedInterval || current.Ordinal == 1
            ? await ResolveCurrentLocalOccurrenceAsync(definition, current, cancellationToken).ConfigureAwait(false)
            : await ResolveCurrentInstantOccurrenceAsync(definition, current, cancellationToken).ConfigureAwait(false);

    private async Task<RecurrenceStep> ResolveCurrentLocalOccurrenceAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence current,
        CancellationToken cancellationToken)
    {
        ScheduleTimeZoneResolution? resolution;
        try
        {
            resolution = await _timeZone.ResolveLocalAsync(
                definition.TimeZone,
                current.ScheduledLocal,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new RecurrenceStep(
                RecurrenceStatus.Unavailable,
                null,
                [],
                "time-zone-unavailable",
                []);
        }

        if (!IsBoundedResolution(resolution))
        {
            return new RecurrenceStep(
                RecurrenceStatus.Corrupt,
                null,
                [],
                "time-zone-evidence-invalid",
                []);
        }

        var exactResolution = resolution!;
        var proofHash = ScheduleRecurrenceProofHash.ComputeLocalResolution(
            definition.TimeZone,
            current.ScheduledLocal,
            exactResolution);
        if (exactResolution.Status is ScheduleTimeZoneResolutionStatus.Unavailable
            or ScheduleTimeZoneResolutionStatus.Backpressured
            or ScheduleTimeZoneResolutionStatus.Corrupt)
        {
            return new RecurrenceStep(
                TimeZoneFailureStatus(exactResolution.Status),
                null,
                [],
                TimeZoneFailureReason(exactResolution.Status),
                [proofHash]);
        }

        var selectedUtc = exactResolution.Status switch
        {
            ScheduleTimeZoneResolutionStatus.Unique
                when exactResolution.ResolvedLocal == current.ScheduledLocal
                    && IsUtc(exactResolution.EarlierUtc)
                    && exactResolution.LaterUtc is null
                => exactResolution.EarlierUtc,
            ScheduleTimeZoneResolutionStatus.AmbiguousLocalTime
                when exactResolution.ResolvedLocal == current.ScheduledLocal
                    && IsUtc(exactResolution.EarlierUtc)
                    && IsUtc(exactResolution.LaterUtc)
                    && exactResolution.EarlierUtc < exactResolution.LaterUtc
                => definition.DaylightSaving.AmbiguousLocalTime == ScheduleAmbiguousLocalTimePolicy.EarlierUtc
                    ? exactResolution.EarlierUtc
                    : exactResolution.LaterUtc,
            ScheduleTimeZoneResolutionStatus.InvalidLocalTime
                when definition.DaylightSaving.InvalidLocalTime == ScheduleInvalidLocalTimePolicy.ShiftForward
                    && exactResolution.ResolvedLocal.Kind == DateTimeKind.Unspecified
                    && exactResolution.ResolvedLocal > current.ScheduledLocal
                    && IsUtc(exactResolution.EarlierUtc)
                    && exactResolution.LaterUtc is null
                => exactResolution.EarlierUtc,
            _ => null,
        };
        if (!string.Equals(
                exactResolution.RulesFingerprint,
                definition.TimeZone.RulesFingerprint,
                StringComparison.Ordinal)
            || selectedUtc != current.ScheduledAtUtc)
        {
            return new RecurrenceStep(
                RecurrenceStatus.Corrupt,
                null,
                [],
                "time-zone-rules-mismatch",
                [proofHash]);
        }

        return new RecurrenceStep(
            RecurrenceStatus.Resolved,
            current,
            [],
            "current-occurrence-resolved",
            [proofHash]);
    }

    private async Task<RecurrenceStep> ResolveCurrentInstantOccurrenceAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence current,
        CancellationToken cancellationToken)
    {
        ScheduleInstantResolution? resolution;
        try
        {
            resolution = await _timeZone.ResolveInstantAsync(
                definition.TimeZone,
                current.ScheduledAtUtc,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new RecurrenceStep(
                RecurrenceStatus.Unavailable,
                null,
                [],
                "time-zone-unavailable",
                []);
        }

        if (!IsBoundedResolution(resolution))
        {
            return new RecurrenceStep(
                RecurrenceStatus.Corrupt,
                null,
                [],
                "time-zone-evidence-invalid",
                []);
        }

        var exactResolution = resolution!;
        var proofHash = ScheduleRecurrenceProofHash.ComputeInstantResolution(
            definition.TimeZone,
            current.ScheduledAtUtc,
            exactResolution);
        if (exactResolution.Status != ScheduleInstantResolutionStatus.Resolved)
        {
            return new RecurrenceStep(
                InstantFailureStatus(exactResolution.Status),
                null,
                [],
                InstantFailureReason(exactResolution.Status),
                [proofHash]);
        }

        if (!string.Equals(
                exactResolution.RulesFingerprint,
                definition.TimeZone.RulesFingerprint,
                StringComparison.Ordinal)
            || exactResolution.ScheduledLocal != current.ScheduledLocal)
        {
            return new RecurrenceStep(
                RecurrenceStatus.Corrupt,
                null,
                [],
                "time-zone-rules-mismatch",
                [proofHash]);
        }

        return new RecurrenceStep(
            RecurrenceStatus.Resolved,
            current,
            [],
            "current-occurrence-resolved",
            [proofHash]);
    }

    private async Task<RecurrenceStep> ResolveNextAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence current,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        if (definition.Recurrence.Kind == ScheduleRecurrenceKind.Once
            || current.Ordinal >= ScheduleContractLimits.MaxOccurrenceOrdinal)
        {
            return new RecurrenceStep(RecurrenceStatus.Exhausted, null, [], "recurrence-exhausted", []);
        }

        if (definition.Recurrence.Kind == ScheduleRecurrenceKind.FixedInterval)
        {
            return await ResolveFixedIntervalAsync(
                definition,
                current,
                cancellationToken).ConfigureAwait(false);
        }

        var periodTicks = definition.Recurrence.Kind == ScheduleRecurrenceKind.Daily
            ? TimeSpan.TicksPerDay
            : 7L * TimeSpan.TicksPerDay;
        var skips = new List<ResolvedSkip>();
        var proofHashes = new List<string>();
        for (var probe = 0; probe < MaxRecurrenceProbes; probe++)
        {
            var ordinal = current.Ordinal + probe + 1L;
            if (ordinal > ScheduleContractLimits.MaxOccurrenceOrdinal)
            {
                return new RecurrenceStep(RecurrenceStatus.Exhausted, null, skips, "recurrence-exhausted", proofHashes);
            }

            var ticks = (decimal)definition.Recurrence.FirstLocalOccurrence.Ticks
                + (ordinal - 1m) * periodTicks;
            if (ticks > MaximumSupportedTicks())
            {
                return new RecurrenceStep(RecurrenceStatus.Exhausted, null, skips, "recurrence-exhausted", proofHashes);
            }

            var nominal = new DateTime((long)ticks, DateTimeKind.Unspecified);
            ScheduleTimeZoneResolution? resolution;
            try
            {
                resolution = await _timeZone.ResolveLocalAsync(
                    definition.TimeZone,
                    nominal,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new RecurrenceStep(RecurrenceStatus.Unavailable, null, skips, "time-zone-unavailable", proofHashes);
            }

            if (!IsBoundedResolution(resolution))
            {
                return new RecurrenceStep(RecurrenceStatus.Corrupt, null, skips, "time-zone-evidence-invalid", proofHashes);
            }

            var exactResolution = resolution!;
            proofHashes.Add(ScheduleRecurrenceProofHash.ComputeLocalResolution(
                definition.TimeZone,
                nominal,
                exactResolution));

            if (exactResolution.Status is ScheduleTimeZoneResolutionStatus.Unavailable
                or ScheduleTimeZoneResolutionStatus.Backpressured
                or ScheduleTimeZoneResolutionStatus.Corrupt)
            {
                return new RecurrenceStep(TimeZoneFailureStatus(exactResolution.Status), null, skips, TimeZoneFailureReason(exactResolution.Status), proofHashes);
            }

            if (!string.Equals(exactResolution.RulesFingerprint, definition.TimeZone.RulesFingerprint, StringComparison.Ordinal))
            {
                return new RecurrenceStep(RecurrenceStatus.Corrupt, null, skips, "time-zone-rules-mismatch", proofHashes);
            }

            DateTimeOffset? selectedUtc = null;
            if (exactResolution.Status == ScheduleTimeZoneResolutionStatus.Unique
                && exactResolution.ResolvedLocal == nominal
                && IsUtc(exactResolution.EarlierUtc)
                && exactResolution.LaterUtc is null)
            {
                selectedUtc = exactResolution.EarlierUtc;
            }
            else if (exactResolution.Status == ScheduleTimeZoneResolutionStatus.AmbiguousLocalTime
                && exactResolution.ResolvedLocal == nominal
                && IsUtc(exactResolution.EarlierUtc)
                && IsUtc(exactResolution.LaterUtc)
                && exactResolution.EarlierUtc < exactResolution.LaterUtc)
            {
                selectedUtc = definition.DaylightSaving.AmbiguousLocalTime == ScheduleAmbiguousLocalTimePolicy.EarlierUtc
                    ? exactResolution.EarlierUtc
                    : exactResolution.LaterUtc;
            }
            else if (exactResolution.Status == ScheduleTimeZoneResolutionStatus.InvalidLocalTime
                && exactResolution.ResolvedLocal.Kind == DateTimeKind.Unspecified
                && exactResolution.ResolvedLocal > nominal
                && IsUtc(exactResolution.EarlierUtc)
                && exactResolution.LaterUtc is null)
            {
                if (definition.DaylightSaving.InvalidLocalTime == ScheduleInvalidLocalTimePolicy.ShiftForward)
                {
                    selectedUtc = exactResolution.EarlierUtc;
                }
                else
                {
                    var evidence = new ScheduleOccurrenceDispositionEvidence(
                        ScheduleOccurrenceDispositionEvidence.CurrentSchemaVersion,
                        ordinal,
                        ordinal,
                        1,
                        nominal,
                        nominal,
                        null,
                        null,
                        definition.TimeZone,
                        ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped,
                        null,
                        "invalid-local-time-skipped",
                        recordedAtUtc);
                    skips.Add(new ResolvedSkip(evidence, exactResolution.EarlierUtc!.Value));
                    continue;
                }
            }
            else
            {
                return new RecurrenceStep(RecurrenceStatus.Corrupt, null, skips, "time-zone-evidence-invalid", proofHashes);
            }

            var occurrence = new ScheduleOccurrence(
                ScheduleOccurrence.CurrentSchemaVersion,
                ordinal,
                nominal,
                selectedUtc!.Value,
                definition.TimeZone);
            return ScheduleContractValidator.ValidateOccurrence(occurrence).IsValid
                ? new RecurrenceStep(RecurrenceStatus.Resolved, occurrence, skips, "recurrence-resolved", proofHashes)
                : new RecurrenceStep(RecurrenceStatus.Corrupt, null, skips, "occurrence-invalid", proofHashes);
        }

        return new RecurrenceStep(RecurrenceStatus.BoundExceeded, null, skips, "recurrence-probe-bound-exceeded", proofHashes);
    }

    private async Task<RecurrenceStep> ResolveFixedIntervalAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence current,
        CancellationToken cancellationToken)
        => await ResolveFixedOrdinalAsync(
            definition,
            current,
            current.Ordinal + 1,
            cancellationToken).ConfigureAwait(false);

    private async Task<RecurrenceStep> ResolveFixedOrdinalAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence current,
        long targetOrdinal,
        CancellationToken cancellationToken)
    {
        if (targetOrdinal <= current.Ordinal
            || targetOrdinal > ScheduleContractLimits.MaxOccurrenceOrdinal)
        {
            return new RecurrenceStep(
                targetOrdinal > ScheduleContractLimits.MaxOccurrenceOrdinal
                    ? RecurrenceStatus.Exhausted
                    : RecurrenceStatus.Corrupt,
                null,
                [],
                targetOrdinal > ScheduleContractLimits.MaxOccurrenceOrdinal
                    ? "recurrence-exhausted"
                    : "recurrence-ordinal-invalid",
                []);
        }

        var ticks = (decimal)current.ScheduledAtUtc.UtcDateTime.Ticks
            + (targetOrdinal - (decimal)current.Ordinal)
            * definition.Recurrence.FixedIntervalSeconds!.Value
            * TimeSpan.TicksPerSecond;
        if (ticks > MaximumSupportedTicks())
        {
            return new RecurrenceStep(RecurrenceStatus.Exhausted, null, [], "recurrence-exhausted", []);
        }

        var nextUtc = new DateTimeOffset((long)ticks, TimeSpan.Zero);
        ScheduleInstantResolution? resolution;
        try
        {
            resolution = await _timeZone.ResolveInstantAsync(
                definition.TimeZone,
                nextUtc,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new RecurrenceStep(RecurrenceStatus.Unavailable, null, [], "time-zone-unavailable", []);
        }

        if (!IsBoundedResolution(resolution))
        {
            return new RecurrenceStep(RecurrenceStatus.Corrupt, null, [], "time-zone-evidence-invalid", []);
        }

        var exactResolution = resolution!;
        var proofHash = ScheduleRecurrenceProofHash.ComputeInstantResolution(
            definition.TimeZone,
            nextUtc,
            exactResolution);

        if (exactResolution.Status != ScheduleInstantResolutionStatus.Resolved)
        {
            return new RecurrenceStep(
                InstantFailureStatus(exactResolution.Status),
                null,
                [],
                InstantFailureReason(exactResolution.Status),
                [proofHash]);
        }

        if (!string.Equals(exactResolution.RulesFingerprint, definition.TimeZone.RulesFingerprint, StringComparison.Ordinal)
            || exactResolution.ScheduledLocal.Kind != DateTimeKind.Unspecified
            || exactResolution.ScheduledLocal.Year is < ScheduleContractLimits.MinimumSupportedYear or > ScheduleContractLimits.MaximumSupportedYear)
        {
            return new RecurrenceStep(RecurrenceStatus.Corrupt, null, [], "time-zone-evidence-invalid", [proofHash]);
        }

        var occurrence = new ScheduleOccurrence(
            ScheduleOccurrence.CurrentSchemaVersion,
            targetOrdinal,
            exactResolution.ScheduledLocal,
            nextUtc,
            definition.TimeZone);
        return ScheduleContractValidator.ValidateOccurrence(occurrence).IsValid
            ? new RecurrenceStep(RecurrenceStatus.Resolved, occurrence, [], "recurrence-resolved", [proofHash])
            : new RecurrenceStep(RecurrenceStatus.Corrupt, null, [], "occurrence-invalid", [proofHash]);
    }

    private async Task<ScheduleCurrentEvidenceResult> ResolveCurrentEvidenceAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence occurrence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ScheduleCurrentEvidenceResult? result;
        try
        {
            result = await _currentEvidence.ResolveAsync(
                definition,
                occurrence,
                now,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ScheduleCurrentEvidenceResult(ScheduleCurrentEvidenceStatus.Unavailable, null);
        }

        if (result is null
            || !Enum.IsDefined(result.Status)
            || result.Status == ScheduleCurrentEvidenceStatus.Unknown
            || result.Status == ScheduleCurrentEvidenceStatus.Available && result.Evidence is null
            || result.Status != ScheduleCurrentEvidenceStatus.Available && result.Evidence is not null)
        {
            return new ScheduleCurrentEvidenceResult(ScheduleCurrentEvidenceStatus.Corrupt, null);
        }

        if (result.Status != ScheduleCurrentEvidenceStatus.Available)
        {
            return result;
        }

        var evidence = result.Evidence!;
        var actor = evidence.ActorContext;
        var authority = evidence.Authority;
        if (!evidence.TryGetResolvedPayload(out var payload)
            || payload is null
            || payload.Length > TriggerDeliveryLimits.MaxInlinePayloadBytes
            || !TriggerDeliveryValidator.ValidateLoopReference(evidence.Target).IsValid
            || !TriggerDeliveryValidator.ValidateAdapterReference(evidence.Adapter).IsValid
            || actor is null
            || !TriggerDeliveryFactory.TryCreateActorContext(
                actor.ActorId,
                actor.SurfaceId,
                actor.WorkspaceId,
                actor.RoleId,
                out _,
                out _)
            || authority is null
            || !TriggerDeliveryValidator.ValidateAuthorityEvidence(authority).IsValid)
        {
            return new ScheduleCurrentEvidenceResult(ScheduleCurrentEvidenceStatus.Corrupt, null);
        }

        var valid = IsSha256(evidence.EvidenceHash)
            && IsUtc(evidence.ObservedAtUtc)
            && evidence.ObservedAtUtc >= now
            && Equals(evidence.Target, definition.Target)
            && Equals(evidence.Adapter, definition.TimeAdapter)
            && Equals(actor.ActorId, definition.ActorId)
            && string.Equals(actor.SurfaceId, definition.SurfaceId, StringComparison.Ordinal)
            && string.Equals(actor.WorkspaceId, definition.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(actor.RoleId, definition.RoleId, StringComparison.Ordinal)
            && Equals(authority.Profile, definition.AuthorityProfile)
            && IsUtc(authority.BoundaryReceipt.EvaluatedAtUtc)
            && authority.BoundaryReceipt.EvaluatedAtUtc <= evidence.ObservedAtUtc
            && evidence.RecurrencePermitted
            && CapabilityIntegrityDigest.Compute(payload).FixedTimeEquals(definition.Payload.ContentHash);
        return valid
            ? result
            : new ScheduleCurrentEvidenceResult(ScheduleCurrentEvidenceStatus.Corrupt, null);
    }

    private static bool TryCreatePrepared(
        ScheduleDefinition definition,
        ScheduleState state,
        SchedulePendingDelivery pending,
        ScheduleCurrentEvidence current,
        string overlapEvidenceHash,
        ScheduleFinalizationPlan plan,
        IReadOnlyList<string> resolutionProofHashes,
        DateTimeOffset now,
        out SchedulePendingDelivery? preparedPending)
    {
        preparedPending = null;
        if (!TriggerDeliveryFactory.TryCreateInlinePayload(current.GetResolvedPayload(), out var payload, out _)
            || !TriggerDeliveryFactory.TryCreateTemporalEvidence(
                now,
                now,
                pending.Occurrence.ScheduledAtUtc,
                null,
                null,
                null,
                null,
                out var temporal,
                out _)
            || !TriggerDeliveryFactory.TryCreateRedeliveryEvidence(
                1,
                1,
                pending.Identity.DeliveryId,
                out var redelivery,
                out _)
            || !TriggerDeliveryFactory.TryCreateEnvelope(
                1,
                pending.Identity.DeliveryId,
                pending.Identity.DeduplicationId,
                TriggerKind.Time,
                current.Adapter,
                current.Target,
                current.ActorContext,
                current.Authority,
                temporal,
                payload,
                redelivery,
                false,
                null,
                TriggerAdmissionStatus.Unknown,
                TriggerAdmissionReason.Unknown,
                out var envelope,
                out _)
            || !TriggerDeliveryHash.TryCompute(envelope, out var envelopeHash, out _))
        {
            return false;
        }

        var prepared = new SchedulePreparedDelivery(
            SchedulePreparedDelivery.CurrentSchemaVersion,
            envelope!,
            envelopeHash!,
            now);
        preparedPending = pending with
        {
            Phase = SchedulePendingDeliveryPhase.Prepared,
            CurrentEvidenceHash = current.EvidenceHash,
            RecurrenceProofHash = ScheduleRecurrenceProofHash.Compute(
                state.DefinitionHash,
                pending.Occurrence,
                plan,
                resolutionProofHashes),
            OverlapEvidenceHash = overlapEvidenceHash,
            FinalizationPlan = plan,
            Prepared = prepared,
            Result = null,
        };
        var preparedState = state with { PendingDelivery = preparedPending };
        return ScheduleContractValidator.ValidatePreparedDeliveryComposition(definition, preparedState).IsValid;
    }

    private async Task<ScheduleEvaluationResult> PersistAsync(
        ScheduleDefinition definition,
        ScheduleState expected,
        ScheduleState replacement,
        ScheduleEvaluationStatus successStatus,
        string successReason,
        CancellationToken cancellationToken)
    {
        if (!ScheduleContractValidator.ValidateDefinitionStateComposition(definition, replacement).IsValid)
        {
            return Result(ScheduleEvaluationStatus.Corrupt, "replacement-state-invalid", expected);
        }

        if (!ScheduleStateTransitionValidator.Validate(definition, expected, replacement).IsValid)
        {
            return Result(ScheduleEvaluationStatus.Corrupt, "replacement-transition-invalid", expected);
        }

        ScheduleStoreMutationResult? mutation;
        try
        {
            mutation = await _store.CompareExchangeAsync(
                new ScheduleStateCompareExchange(expected, replacement),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(ScheduleEvaluationStatus.Unavailable, "schedule-store-unavailable", expected);
        }

        if (!IsValidCompareExchangeResult(definition, mutation))
        {
            return Result(ScheduleEvaluationStatus.Corrupt, "schedule-store-evidence-invalid", expected);
        }

        if (mutation.Status == ScheduleStoreMutationStatus.Applied)
        {
            var current = mutation.CurrentState!;
            var exactReplacement = ScheduleContractHash.TryComputeState(replacement, out var replacementHash, out _)
                && ScheduleContractHash.TryComputeState(current, out var currentHash, out _)
                && string.Equals(replacementHash, currentHash, StringComparison.Ordinal);
            return exactReplacement
                && ScheduleContractValidator.ValidateDefinitionStateComposition(definition, current).IsValid
                ? Result(successStatus, successReason, current)
                : Result(ScheduleEvaluationStatus.Corrupt, "stored-state-invalid", current);
        }

        return Result(
            MutationFailureStatus(mutation.Status),
            MutationFailureReason(mutation.Status),
            mutation.CurrentState ?? expected);
    }

    private static ScheduleOccurrenceDispositionEvidence Disposition(
        ScheduleOccurrence occurrence,
        ScheduleOccurrenceDisposition disposition,
        string reasonCode,
        DateTimeOffset recordedAtUtc,
        string? decisionEvidenceHash = null)
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
            decisionEvidenceHash,
            reasonCode,
            recordedAtUtc);

    private static bool IsValidReadResult(ScheduleId requestedScheduleId, ScheduleStoreReadResult? result)
        => result is not null
            && Enum.IsDefined(result.Status)
            && result.Status != ScheduleStoreReadStatus.Unknown
            && (result.Status == ScheduleStoreReadStatus.Found
                ? result.Definition is not null
                    && result.State is not null
                    && Equals(result.Definition.ScheduleId, requestedScheduleId)
                    && Equals(result.State.ScheduleId, requestedScheduleId)
                : result.Definition is null && result.State is null);

    private static bool IsValidCompareExchangeResult(
        ScheduleDefinition definition,
        ScheduleStoreMutationResult? result)
        => result is not null
            && Enum.IsDefined(result.Status)
            && result.Status is not (ScheduleStoreMutationStatus.Unknown or ScheduleStoreMutationStatus.AlreadyExists)
            && (result.Status != ScheduleStoreMutationStatus.Applied || result.CurrentState is not null)
            && (result.CurrentState is null
                || ScheduleContractValidator.ValidateDefinitionStateComposition(definition, result.CurrentState).IsValid);

    private static bool QueueStatusReasonMatches(
        TriggerQueueAdmissionStatus status,
        TriggerQueueAdmissionReason reason)
        => status switch
        {
            TriggerQueueAdmissionStatus.Queued => reason == TriggerQueueAdmissionReason.Enqueued,
            TriggerQueueAdmissionStatus.Replayed => reason == TriggerQueueAdmissionReason.ExactReplay,
            TriggerQueueAdmissionStatus.Rejected => reason is TriggerQueueAdmissionReason.AdmissionRejected
                or TriggerQueueAdmissionReason.IdentityConflict,
            TriggerQueueAdmissionStatus.Backpressured => reason is TriggerQueueAdmissionReason.EntryBytesExceeded
                or TriggerQueueAdmissionReason.QueueCountExceeded
                or TriggerQueueAdmissionReason.QueueBytesExceeded
                or TriggerQueueAdmissionReason.LoopQuotaExceeded
                or TriggerQueueAdmissionReason.RetainedEvidenceExceeded
                or TriggerQueueAdmissionReason.DurabilityTombstoneCapacityExceeded,
            TriggerQueueAdmissionStatus.Unavailable => reason is TriggerQueueAdmissionReason.AdmissionUnavailable
                or TriggerQueueAdmissionReason.StorageUnavailable,
            _ => false,
        };

    private static bool AdmissionEvidenceMatches(
        TriggerAdmissionStatus? status,
        TriggerAdmissionReason? reason)
    {
        if (!status.HasValue || !reason.HasValue)
        {
            return false;
        }

        return status.Value switch
        {
            TriggerAdmissionStatus.Unknown => false,
            TriggerAdmissionStatus.Admitted => reason.Value == TriggerAdmissionReason.EvidenceAccepted,
            TriggerAdmissionStatus.Replayed => reason.Value == TriggerAdmissionReason.ExactReplay,
            TriggerAdmissionStatus.Conflicting => reason.Value == TriggerAdmissionReason.IdentityConflict,
            TriggerAdmissionStatus.NotYetEligible => reason.Value == TriggerAdmissionReason.NotBefore,
            TriggerAdmissionStatus.Expired => reason.Value is TriggerAdmissionReason.DeadlineExceeded
                or TriggerAdmissionReason.Expired,
            TriggerAdmissionStatus.Unauthorized => reason.Value is TriggerAdmissionReason.StaleLoop
                or TriggerAdmissionReason.StaleAdapter
                or TriggerAdmissionReason.ActorMismatch
                or TriggerAdmissionReason.SurfaceMismatch
                or TriggerAdmissionReason.WorkspaceMismatch
                or TriggerAdmissionReason.RoleMismatch
                or TriggerAdmissionReason.AuthorityMismatch
                or TriggerAdmissionReason.StaleAuthority
                or TriggerAdmissionReason.AuthorityBoundary
                or TriggerAdmissionReason.StaleDelivery,
            TriggerAdmissionStatus.Unavailable => reason.Value is TriggerAdmissionReason.AdapterUnavailable
                or TriggerAdmissionReason.HistoryUnavailable,
            TriggerAdmissionStatus.Invalid => reason.Value == TriggerAdmissionReason.InvalidEnvelope,
            _ => false,
        };
    }

    private static bool QueueAdmissionCoheres(TriggerQueueAdmissionResult result)
    {
        if (!result.AdmissionStatus.HasValue)
        {
            return true;
        }

        var status = result.AdmissionStatus.Value;
        var accepted = status is TriggerAdmissionStatus.Admitted
            or TriggerAdmissionStatus.Replayed
            or TriggerAdmissionStatus.NotYetEligible;
        return result.Status switch
        {
            TriggerQueueAdmissionStatus.Queued => accepted,
            TriggerQueueAdmissionStatus.Replayed => accepted,
            TriggerQueueAdmissionStatus.Rejected when result.Reason == TriggerQueueAdmissionReason.IdentityConflict
                => accepted || status == TriggerAdmissionStatus.Conflicting,
            TriggerQueueAdmissionStatus.Rejected => status is TriggerAdmissionStatus.Expired
                or TriggerAdmissionStatus.Unauthorized
                or TriggerAdmissionStatus.Invalid,
            TriggerQueueAdmissionStatus.Backpressured => accepted,
            TriggerQueueAdmissionStatus.Unavailable when result.Reason == TriggerQueueAdmissionReason.AdmissionUnavailable
                => status == TriggerAdmissionStatus.Unavailable,
            TriggerQueueAdmissionStatus.Unavailable => true,
            _ => false,
        };
    }

    private static bool QueueResultMatches(
        TriggerQueueAdmissionResult? result,
        SchedulePendingDelivery pending,
        SchedulePreparedDelivery prepared)
        => result is not null
            && Enum.IsDefined(result.Status)
            && Enum.IsDefined(result.Reason)
            && (!result.AdmissionStatus.HasValue || Enum.IsDefined(result.AdmissionStatus.Value))
            && (!result.AdmissionReason.HasValue || Enum.IsDefined(result.AdmissionReason.Value))
            && QueueStatusReasonMatches(result.Status, result.Reason)
            && AdmissionEvidenceMatches(result.AdmissionStatus, result.AdmissionReason)
            && QueueAdmissionCoheres(result)
            && Equals(result.DeliveryId, pending.Identity.DeliveryId)
            && Equals(result.DeduplicationId, pending.Identity.DeduplicationId)
            && string.Equals(result.CanonicalEnvelopeHash, prepared.CanonicalEnvelopeHash, StringComparison.Ordinal)
            && result.Status is TriggerQueueAdmissionStatus.Queued
                or TriggerQueueAdmissionStatus.Replayed
                or TriggerQueueAdmissionStatus.Rejected
                or TriggerQueueAdmissionStatus.Backpressured
                or TriggerQueueAdmissionStatus.Unavailable;

    private static bool TryNextRevision(ScheduleState state, out long revision)
    {
        revision = state.StateRevision + 1;
        return state.StateRevision < ScheduleContractLimits.MaxRevision;
    }

    private static long MaximumSupportedTicks()
        => new DateTime(
            ScheduleContractLimits.MaximumSupportedYear + 1,
            1,
            1,
            0,
            0,
            0,
            DateTimeKind.Unspecified).Ticks - 1;

    private static bool IsUtc(DateTimeOffset? value)
        => value is { Offset: { } offset }
            && offset == TimeSpan.Zero
            && value.Value.Year is >= ScheduleContractLimits.MinimumSupportedYear and <= ScheduleContractLimits.MaximumSupportedYear;

    private static bool IsSha256(string? value)
        => value?.Length == ScheduleContractLimits.Sha256HexCharacters
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsBoundedResolution(ScheduleTimeZoneResolution? resolution)
        => resolution is not null
            && Enum.IsDefined(resolution.Status)
            && resolution.Status != ScheduleTimeZoneResolutionStatus.Unknown
            && (resolution.RulesFingerprint is null || IsSha256(resolution.RulesFingerprint));

    private static bool IsBoundedResolution(ScheduleInstantResolution? resolution)
        => resolution is not null
            && Enum.IsDefined(resolution.Status)
            && resolution.Status != ScheduleInstantResolutionStatus.Unknown
            && (resolution.RulesFingerprint is null || IsSha256(resolution.RulesFingerprint));

    private static TriggerQueuePriority QueuePriority(SchedulePriority priority)
        => priority switch
        {
            SchedulePriority.Background => TriggerQueuePriority.Background,
            SchedulePriority.Normal => TriggerQueuePriority.Normal,
            SchedulePriority.Elevated => TriggerQueuePriority.Elevated,
            SchedulePriority.Critical => TriggerQueuePriority.Critical,
            _ => throw new InvalidOperationException("A validated schedule has a closed queue priority."),
        };

    private static ScheduleEvaluationStatus ReadFailureStatus(ScheduleStoreReadStatus status)
        => status switch
        {
            ScheduleStoreReadStatus.Backpressured => ScheduleEvaluationStatus.Backpressured,
            ScheduleStoreReadStatus.Corrupt => ScheduleEvaluationStatus.Corrupt,
            _ => ScheduleEvaluationStatus.Unavailable,
        };

    private static string ReadFailureReason(ScheduleStoreReadStatus status)
        => status switch
        {
            ScheduleStoreReadStatus.Backpressured => "schedule-store-backpressured",
            ScheduleStoreReadStatus.Corrupt => "schedule-store-corrupt",
            _ => "schedule-store-unavailable",
        };

    private static ScheduleEvaluationStatus MutationFailureStatus(ScheduleStoreMutationStatus status)
        => status switch
        {
            ScheduleStoreMutationStatus.Conflict => ScheduleEvaluationStatus.Conflict,
            ScheduleStoreMutationStatus.Backpressured => ScheduleEvaluationStatus.Backpressured,
            ScheduleStoreMutationStatus.Corrupt => ScheduleEvaluationStatus.Corrupt,
            _ => ScheduleEvaluationStatus.Unavailable,
        };

    private static string MutationFailureReason(ScheduleStoreMutationStatus status)
        => status switch
        {
            ScheduleStoreMutationStatus.Conflict => "schedule-state-conflict",
            ScheduleStoreMutationStatus.Backpressured => "schedule-store-backpressured",
            ScheduleStoreMutationStatus.Corrupt => "schedule-store-corrupt",
            _ => "schedule-store-unavailable",
        };

    private static ScheduleEvaluationStatus CurrentEvidenceFailureStatus(ScheduleCurrentEvidenceStatus status)
        => status switch
        {
            ScheduleCurrentEvidenceStatus.PermissionDenied or ScheduleCurrentEvidenceStatus.RecurrenceDenied
                => ScheduleEvaluationStatus.PermissionDenied,
            ScheduleCurrentEvidenceStatus.Backpressured => ScheduleEvaluationStatus.Backpressured,
            ScheduleCurrentEvidenceStatus.Corrupt => ScheduleEvaluationStatus.Corrupt,
            _ => ScheduleEvaluationStatus.Unavailable,
        };

    private static string CurrentEvidenceFailureReason(ScheduleCurrentEvidenceStatus status)
        => status switch
        {
            ScheduleCurrentEvidenceStatus.PermissionDenied => "schedule-permission-denied",
            ScheduleCurrentEvidenceStatus.RecurrenceDenied => "schedule-recurrence-denied",
            ScheduleCurrentEvidenceStatus.TargetUnavailable => "schedule-target-unavailable",
            ScheduleCurrentEvidenceStatus.AdapterUnavailable => "schedule-adapter-unavailable",
            ScheduleCurrentEvidenceStatus.ActorUnavailable => "schedule-actor-unavailable",
            ScheduleCurrentEvidenceStatus.AuthorityUnavailable => "schedule-authority-unavailable",
            ScheduleCurrentEvidenceStatus.PayloadUnavailable => "schedule-payload-unavailable",
            ScheduleCurrentEvidenceStatus.Backpressured => "schedule-evidence-backpressured",
            ScheduleCurrentEvidenceStatus.Corrupt => "schedule-evidence-corrupt",
            _ => "schedule-evidence-unavailable",
        };

    private static ScheduleEvaluationStatus OverlapFailureStatus(ScheduleOverlapStatus status)
        => status switch
        {
            ScheduleOverlapStatus.Backpressured => ScheduleEvaluationStatus.Backpressured,
            ScheduleOverlapStatus.Corrupt => ScheduleEvaluationStatus.Corrupt,
            _ => ScheduleEvaluationStatus.Unavailable,
        };

    private static string OverlapFailureReason(ScheduleOverlapStatus status)
        => status switch
        {
            ScheduleOverlapStatus.Backpressured => "overlap-evidence-backpressured",
            ScheduleOverlapStatus.Corrupt => "overlap-evidence-corrupt",
            _ => "overlap-evidence-unavailable",
        };

    private static RecurrenceStatus TimeZoneFailureStatus(ScheduleTimeZoneResolutionStatus status)
        => status switch
        {
            ScheduleTimeZoneResolutionStatus.Backpressured => RecurrenceStatus.Backpressured,
            ScheduleTimeZoneResolutionStatus.Corrupt => RecurrenceStatus.Corrupt,
            _ => RecurrenceStatus.Unavailable,
        };

    private static string TimeZoneFailureReason(ScheduleTimeZoneResolutionStatus status)
        => status switch
        {
            ScheduleTimeZoneResolutionStatus.Backpressured => "time-zone-backpressured",
            ScheduleTimeZoneResolutionStatus.Corrupt => "time-zone-corrupt",
            _ => "time-zone-unavailable",
        };

    private static RecurrenceStatus InstantFailureStatus(ScheduleInstantResolutionStatus status)
        => status switch
        {
            ScheduleInstantResolutionStatus.Backpressured => RecurrenceStatus.Backpressured,
            ScheduleInstantResolutionStatus.Corrupt => RecurrenceStatus.Corrupt,
            _ => RecurrenceStatus.Unavailable,
        };

    private static string InstantFailureReason(ScheduleInstantResolutionStatus status)
        => status switch
        {
            ScheduleInstantResolutionStatus.Backpressured => "time-zone-backpressured",
            ScheduleInstantResolutionStatus.Corrupt => "time-zone-corrupt",
            _ => "time-zone-unavailable",
        };

    private static ScheduleEvaluationStatus RecurrenceFailureStatus(RecurrenceStatus status)
        => status switch
        {
            RecurrenceStatus.Backpressured => ScheduleEvaluationStatus.Backpressured,
            RecurrenceStatus.BoundExceeded => ScheduleEvaluationStatus.BoundExceeded,
            RecurrenceStatus.Corrupt => ScheduleEvaluationStatus.Corrupt,
            _ => ScheduleEvaluationStatus.Unavailable,
        };

    private static ScheduleEvaluationResult Result(
        ScheduleEvaluationStatus status,
        string reasonCode,
        ScheduleState? state)
        => new(status, reasonCode, ScheduleContractCopy.Copy(state));

    private enum RecurrenceStatus
    {
        Resolved,
        Exhausted,
        Unavailable,
        Backpressured,
        Corrupt,
        BoundExceeded,
    }

    private sealed record ResolvedSkip(
        ScheduleOccurrenceDispositionEvidence Evidence,
        DateTimeOffset EffectiveAtUtc);

    private sealed record RecurrenceStep(
        RecurrenceStatus Status,
        ScheduleOccurrence? NextOccurrence,
        IReadOnlyList<ResolvedSkip> Skips,
        string ReasonCode,
        IReadOnlyList<string> ProofHashes);

    private sealed record RecurrenceScan(
        RecurrenceStatus Status,
        IReadOnlyList<ScheduleOccurrence> DueOccurrences,
        IReadOnlyList<ResolvedSkip> Skips,
        ScheduleOccurrence? FirstFutureOccurrence,
        long LatestDueOrdinal,
        string ReasonCode,
        IReadOnlyList<string> ProofHashes);

    private sealed record PlanBuild(
        RecurrenceStatus Status,
        ScheduleFinalizationPlan? Plan,
        ScheduleCatchUpEpisode? CurrentEpisode,
        string ReasonCode,
        IReadOnlyList<string> ProofHashes);
}
