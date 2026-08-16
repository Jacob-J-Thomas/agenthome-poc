using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Validates one exact successor of a durable schedule state, including its bounded terminal-evidence rollover.</summary>
/// <remarks>
/// Structural state validation is insufficient at a persistence boundary: two individually valid snapshots can still
/// erase evidence, rewind recurrence, resurrect exhausted work, or replace an uncertain delivery. This validator keeps
/// the optimistic state machine closed while leaving current authority and time-zone resolution in Application.
/// </remarks>
public static class ScheduleStateTransitionValidator
{
    /// <summary>Validates one contiguous state successor against its immutable definition.</summary>
    public static ScheduleContractValidationResult Validate(
        ScheduleDefinition? definition,
        ScheduleState? current,
        ScheduleState? next)
    {
        var errors = new List<ScheduleContractError>();
        AddComposition(errors, "current", ScheduleContractValidator.ValidateDefinitionStateComposition(definition, current));
        AddComposition(errors, "next", ScheduleContractValidator.ValidateDefinitionStateComposition(definition, next));
        if (errors.Count != 0)
        {
            return Result(errors);
        }

        var before = current!;
        var after = next!;
        if (!Equals(before.ScheduleId, after.ScheduleId)
            || before.DefinitionRevision != after.DefinitionRevision
            || !string.Equals(before.DefinitionHash, after.DefinitionHash, StringComparison.Ordinal))
        {
            Add(errors, "immutable_coordinates_changed", "next.definitionHash");
        }

        if (before.StateRevision >= ScheduleContractLimits.MaxRevision
            || after.StateRevision != before.StateRevision + 1)
        {
            Add(errors, "invalid_successor_revision", "next.stateRevision");
        }

        if (before.LastClockObservedAtUtc is { } priorClock
            && (after.LastClockObservedAtUtc is null || after.LastClockObservedAtUtc < priorClock))
        {
            Add(errors, "clock_regressed", "next.lastClockObservedAtUtc");
        }

        if (!TryGetAppendedItems(
                before.DispositionEvidence,
                after.DispositionEvidence,
                out var appendedDispositionEvidence))
        {
            Add(errors, "disposition_evidence_rewritten", "next.dispositionEvidence");
        }

        if (!TryGetAppendedTerminalEvidence(
                before.TerminalDeliveryEvidence,
                after.TerminalDeliveryEvidence,
                out var appendedTerminalEvidence))
        {
            Add(errors, "terminal_evidence_rewritten", "next.terminalDeliveryEvidence");
        }

        if (errors.Count != 0)
        {
            return Result(errors);
        }

        var legal = IsObservationOrControl(before, after)
            || IsClaim(before, after)
            || IsPreparation(before, after)
            || IsResultObservation(before, after)
            || IsFinalization(before, after, appendedDispositionEvidence, appendedTerminalEvidence)
            || IsClaimDisposition(definition!, before, after, appendedDispositionEvidence, appendedTerminalEvidence);
        if (!legal)
        {
            Add(errors, "illegal_state_transition", "next");
        }

        return Result(errors);
    }

    private static bool IsObservationOrControl(ScheduleState current, ScheduleState next)
    {
        var enabledChanged = current.Enabled != next.Enabled;
        var clockChanged = current.LastClockObservedAtUtc != next.LastClockObservedAtUtc;
        if (enabledChanged == clockChanged)
        {
            return false;
        }

        var projected = next with
        {
            StateRevision = current.StateRevision,
            Enabled = current.Enabled,
            LastClockObservedAtUtc = current.LastClockObservedAtUtc,
        };
        return SameState(current, projected);
    }

    private static bool IsClaim(ScheduleState current, ScheduleState next)
    {
        if (!current.Enabled
            || current.PendingDelivery is not null
            || next.PendingDelivery?.Phase != SchedulePendingDeliveryPhase.Claimed
            || next.LastClockObservedAtUtc != next.PendingDelivery.ClaimedAtUtc)
        {
            return false;
        }

        var projected = next with
        {
            StateRevision = current.StateRevision,
            LastClockObservedAtUtc = current.LastClockObservedAtUtc,
            PendingDelivery = null,
        };
        return SameState(current, projected);
    }

    private static bool IsPreparation(ScheduleState current, ScheduleState next)
    {
        var prior = current.PendingDelivery;
        var prepared = next.PendingDelivery;
        if (prior?.Phase != SchedulePendingDeliveryPhase.Claimed
            || prepared?.Phase != SchedulePendingDeliveryPhase.Prepared
            || prepared.Prepared?.PreparedAtUtc != next.LastClockObservedAtUtc
            || current.CatchUpEpisode is not null && next.CatchUpEpisode != current.CatchUpEpisode)
        {
            return false;
        }

        var projectedPending = prepared with
        {
            Phase = prior.Phase,
            CurrentEvidenceHash = prior.CurrentEvidenceHash,
            RecurrenceProofHash = prior.RecurrenceProofHash,
            OverlapEvidenceHash = prior.OverlapEvidenceHash,
            FinalizationPlan = prior.FinalizationPlan,
            Prepared = prior.Prepared,
            Result = prior.Result,
        };
        var projected = next with
        {
            StateRevision = current.StateRevision,
            LastClockObservedAtUtc = current.LastClockObservedAtUtc,
            CatchUpEpisode = current.CatchUpEpisode,
            PendingDelivery = projectedPending,
        };
        return SameState(current, projected);
    }

    private static bool IsResultObservation(ScheduleState current, ScheduleState next)
    {
        var prior = current.PendingDelivery;
        var observed = next.PendingDelivery;
        if (observed?.Phase != SchedulePendingDeliveryPhase.ResultObserved
            || prior?.Phase is not (SchedulePendingDeliveryPhase.Prepared or SchedulePendingDeliveryPhase.ResultObserved)
            || observed.Result?.RecordedAtUtc != next.LastClockObservedAtUtc)
        {
            return false;
        }

        if (prior.Phase == SchedulePendingDeliveryPhase.ResultObserved
            && prior.Result?.Kind is not (ScheduleDeliveryResultKind.Backpressured or ScheduleDeliveryResultKind.Unavailable))
        {
            return false;
        }

        var projectedPending = observed with
        {
            Phase = prior.Phase,
            CurrentEvidenceHash = prior.CurrentEvidenceHash,
            Result = prior.Result,
        };
        var projected = next with
        {
            StateRevision = current.StateRevision,
            LastClockObservedAtUtc = current.LastClockObservedAtUtc,
            PendingDelivery = projectedPending,
        };
        return SameState(current, projected);
    }

    private static bool IsFinalization(
        ScheduleState current,
        ScheduleState next,
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence> appendedDispositionEvidence,
        IReadOnlyList<ScheduleTerminalDeliveryEvidence> appendedTerminalEvidence)
    {
        var pending = current.PendingDelivery;
        var plan = pending?.FinalizationPlan;
        if (pending?.Phase != SchedulePendingDeliveryPhase.ResultObserved
            || pending.Result?.Kind is not (ScheduleDeliveryResultKind.Queued
                or ScheduleDeliveryResultKind.Replayed
                or ScheduleDeliveryResultKind.Rejected)
            || plan is null
            || next.PendingDelivery is not null
            || appendedTerminalEvidence.Count != 1
            || appendedDispositionEvidence.Count != plan.DispositionEvidence.Count
            || next.LastClockObservedAtUtc is not { } finalizedAtUtc)
        {
            return false;
        }

        var appendedTerminal = appendedTerminalEvidence[0];
        var expectedTerminal = new ScheduleTerminalDeliveryEvidence(
            ScheduleTerminalDeliveryEvidence.CurrentSchemaVersion,
            pending.Occurrence,
            pending.Identity,
            pending.CurrentEvidenceHash!,
            pending.RecurrenceProofHash!,
            pending.OverlapEvidenceHash!,
            pending.Result,
            finalizedAtUtc);
        if (appendedTerminal != expectedTerminal
            || !appendedDispositionEvidence.SequenceEqual(plan.DispositionEvidence))
        {
            return false;
        }

        var expected = current with
        {
            StateRevision = next.StateRevision,
            NextOccurrence = plan.NextOccurrence,
            CatchUpEpisode = plan.CatchUpEpisode,
            DeferredOccurrence = plan.DeferredOccurrence,
            LastClockObservedAtUtc = finalizedAtUtc,
            PendingDelivery = null,
            DispositionEvidence = current.DispositionEvidence.Concat(plan.DispositionEvidence).ToArray(),
            TerminalDeliveryEvidence = next.TerminalDeliveryEvidence,
        };
        if (!SameState(expected, next))
        {
            return false;
        }

        var droppedTerminalCount = current.TerminalDeliveryEvidence.Count + 1 - next.TerminalDeliveryEvidence.Count;
        if (droppedTerminalCount <= 0)
        {
            return true;
        }

        var lessCompactedTerminal = current.TerminalDeliveryEvidence
            .Skip(droppedTerminalCount - 1)
            .Append(expectedTerminal)
            .ToArray();
        if (lessCompactedTerminal.Length > ScheduleContractLimits.RetainedTerminalDeliveryEvidenceItems)
        {
            return true;
        }

        var lessCompacted = expected with { TerminalDeliveryEvidence = lessCompactedTerminal };
        return !ScheduleContractHash.TryComputeState(lessCompacted, out _, out var validation)
            && validation.Errors.Count == 1
            && validation.Errors[0].Code == "canonical_document_too_large";
    }

    private static bool IsClaimDisposition(
        ScheduleDefinition definition,
        ScheduleState current,
        ScheduleState next,
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence> appendedDispositionEvidence,
        IReadOnlyList<ScheduleTerminalDeliveryEvidence> appendedTerminalEvidence)
    {
        var pending = current.PendingDelivery;
        if (pending?.Phase != SchedulePendingDeliveryPhase.Claimed
            || next.PendingDelivery is not null
            || appendedTerminalEvidence.Count != 0)
        {
            return false;
        }

        var projected = next with
        {
            StateRevision = current.StateRevision,
            NextOccurrence = current.NextOccurrence,
            CatchUpEpisode = current.CatchUpEpisode,
            DeferredOccurrence = current.DeferredOccurrence,
            LastClockObservedAtUtc = current.LastClockObservedAtUtc,
            PendingDelivery = current.PendingDelivery,
            DispositionEvidence = current.DispositionEvidence,
        };
        if (!SameState(current, projected))
        {
            return false;
        }

        if (IsDefer(current, next, appendedDispositionEvidence))
        {
            return true;
        }

        if (appendedDispositionEvidence.Count == 0
            || next.DeferredOccurrence is not null
            || next.NextOccurrence is { } successor && successor.Ordinal <= pending.Occurrence.Ordinal
            || next.NextOccurrence is null && !MayExhaustAfter(definition, pending.Occurrence))
        {
            return false;
        }

        var exactCurrentSkip = appendedDispositionEvidence.Any(item => item.FirstOrdinal == pending.Occurrence.Ordinal
            && item.LastOrdinal == pending.Occurrence.Ordinal
            && item.Count == 1
            && item.FirstScheduledLocal == pending.Occurrence.ScheduledLocal
            && item.FirstScheduledAtUtc == pending.Occurrence.ScheduledAtUtc
            && item.RecordedAtUtc == next.LastClockObservedAtUtc
            && item.Disposition is ScheduleOccurrenceDisposition.MisfireSkipped or ScheduleOccurrenceDisposition.OverlapSkipped);
        if (!exactCurrentSkip
            || appendedDispositionEvidence.Any(item => item.RecordedAtUtc != next.LastClockObservedAtUtc)
            || !IsExactRecurrenceAdvance(
                definition.Recurrence,
                pending.Occurrence,
                next.NextOccurrence,
                appendedDispositionEvidence)
            || !IsExactCatchUpAdvance(current.CatchUpEpisode, next.CatchUpEpisode))
        {
            return false;
        }

        var exclusiveEnd = next.NextOccurrence?.Ordinal ?? pending.Occurrence.Ordinal + 1;
        return CoversContiguousOrdinals(
            appendedDispositionEvidence,
            pending.Occurrence.Ordinal,
            exclusiveEnd);
    }

    private static bool IsExactRecurrenceAdvance(
        ScheduleRecurrenceRule recurrence,
        ScheduleOccurrence current,
        ScheduleOccurrence? next,
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence> appended)
    {
        if (next is not null && !MatchesRecurrenceCoordinate(recurrence, current, next))
        {
            return false;
        }

        if (recurrence.Kind != ScheduleRecurrenceKind.FixedInterval)
        {
            return true;
        }

        foreach (var evidence in appended)
        {
            if (evidence.FirstScheduledAtUtc is { } first
                && !MatchesFixedUtc(recurrence, current, evidence.FirstOrdinal, first)
                || evidence.LastScheduledAtUtc is { } last
                && !MatchesFixedUtc(recurrence, current, evidence.LastOrdinal, last))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesRecurrenceCoordinate(
        ScheduleRecurrenceRule recurrence,
        ScheduleOccurrence current,
        ScheduleOccurrence next)
    {
        if (next.Ordinal <= current.Ordinal
            || next.ScheduledAtUtc <= current.ScheduledAtUtc)
        {
            return false;
        }

        var ordinalDelta = next.Ordinal - current.Ordinal;
        return recurrence.Kind switch
        {
            ScheduleRecurrenceKind.FixedInterval
                => MatchesFixedUtc(recurrence, current, next.Ordinal, next.ScheduledAtUtc),
            ScheduleRecurrenceKind.Daily
                => (decimal)(next.ScheduledLocal.Ticks - current.ScheduledLocal.Ticks)
                    == (decimal)ordinalDelta * TimeSpan.TicksPerDay,
            ScheduleRecurrenceKind.Weekly
                => (decimal)(next.ScheduledLocal.Ticks - current.ScheduledLocal.Ticks)
                    == (decimal)ordinalDelta * 7 * TimeSpan.TicksPerDay,
            _ => false,
        };
    }

    private static bool MatchesFixedUtc(
        ScheduleRecurrenceRule recurrence,
        ScheduleOccurrence current,
        long ordinal,
        DateTimeOffset scheduledAtUtc)
        => recurrence.FixedIntervalSeconds is { } interval
            && ordinal >= current.Ordinal
            && (decimal)(scheduledAtUtc.UtcDateTime.Ticks - current.ScheduledAtUtc.UtcDateTime.Ticks)
                == (decimal)(ordinal - current.Ordinal) * interval * TimeSpan.TicksPerSecond;

    private static bool IsExactCatchUpAdvance(
        ScheduleCatchUpEpisode? current,
        ScheduleCatchUpEpisode? next)
    {
        if (current is null)
        {
            return next is null;
        }

        if (current.RemainingAdmittedOccurrences == 1)
        {
            return next is null;
        }

        return next is not null
            && next.LatestDueOrdinal == current.LatestDueOrdinal
            && next.RemainingAdmittedOccurrences == current.RemainingAdmittedOccurrences - 1;
    }

    private static bool IsDefer(
        ScheduleState current,
        ScheduleState next,
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence> appended)
    {
        var pending = current.PendingDelivery!;
        if (next.NextOccurrence != current.NextOccurrence
            || next.CatchUpEpisode != current.CatchUpEpisode
            || next.DeferredOccurrence is null
            || next.DeferredOccurrence.Occurrence != pending.Occurrence
            || next.DeferredOccurrence.Identity != pending.Identity
            || next.DeferredOccurrence.DeferredAtUtc > next.LastClockObservedAtUtc)
        {
            return false;
        }

        if (current.DeferredOccurrence is not null)
        {
            return next.DeferredOccurrence == current.DeferredOccurrence && appended.Count == 0;
        }

        return appended.Count == 1
            && appended[0].Disposition == ScheduleOccurrenceDisposition.OverlapDeferred
            && appended[0].FirstOrdinal == pending.Occurrence.Ordinal
            && appended[0].LastOrdinal == pending.Occurrence.Ordinal
            && appended[0].RecordedAtUtc == next.LastClockObservedAtUtc
            && next.DeferredOccurrence.DeferredAtUtc == next.LastClockObservedAtUtc;
    }

    private static bool CoversContiguousOrdinals(
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence> evidence,
        long first,
        long exclusiveEnd)
    {
        var expected = first;
        foreach (var item in evidence)
        {
            if (item.FirstOrdinal != expected)
            {
                return false;
            }

            if (item.LastOrdinal == long.MaxValue)
            {
                return false;
            }

            expected = item.LastOrdinal + 1;
        }

        return expected == exclusiveEnd;
    }

    private static bool MayExhaustAfter(ScheduleDefinition definition, ScheduleOccurrence occurrence)
    {
        if (definition.Recurrence.Kind == ScheduleRecurrenceKind.Once
            || occurrence.Ordinal >= ScheduleContractLimits.MaxOccurrenceOrdinal)
        {
            return true;
        }

        decimal requiredTicks = definition.Recurrence.Kind switch
        {
            ScheduleRecurrenceKind.FixedInterval when definition.Recurrence.FixedIntervalSeconds is { } seconds
                => (decimal)seconds * TimeSpan.TicksPerSecond,
            ScheduleRecurrenceKind.Daily => TimeSpan.TicksPerDay,
            ScheduleRecurrenceKind.Weekly => 7m * TimeSpan.TicksPerDay,
            _ => 0,
        };
        if (requiredTicks <= 0)
        {
            return false;
        }

        var currentTicks = definition.Recurrence.Kind == ScheduleRecurrenceKind.FixedInterval
            ? occurrence.ScheduledAtUtc.UtcTicks
            : occurrence.ScheduledLocal.Ticks;
        return (decimal)currentTicks + requiredTicks > MaximumSupportedTicks();
    }

    private static long MaximumSupportedTicks()
        => new DateTime(
            ScheduleContractLimits.MaximumSupportedYear,
            12,
            31,
            23,
            59,
            59,
            999,
            DateTimeKind.Utc).AddTicks(TimeSpan.TicksPerMillisecond - 1).Ticks;

    private static bool SameState(ScheduleState left, ScheduleState right)
        => ScheduleContractHash.TryComputeState(left, out var leftHash, out _)
            && ScheduleContractHash.TryComputeState(right, out var rightHash, out _)
            && string.Equals(leftHash, rightHash, StringComparison.Ordinal);

    private static bool TryGetAppendedItems<T>(
        IReadOnlyList<T> current,
        IReadOnlyList<T> next,
        out IReadOnlyList<T> appended)
    {
        appended = [];
        if (next.Count < current.Count)
        {
            return false;
        }

        var remaining = next.ToList();
        foreach (var retained in current)
        {
            var index = remaining.FindIndex(candidate => EqualityComparer<T>.Default.Equals(candidate, retained));
            if (index < 0)
            {
                return false;
            }

            remaining.RemoveAt(index);
        }

        appended = remaining;
        return true;
    }

    private static bool TryGetAppendedTerminalEvidence(
        IReadOnlyList<ScheduleTerminalDeliveryEvidence> current,
        IReadOnlyList<ScheduleTerminalDeliveryEvidence> next,
        out IReadOnlyList<ScheduleTerminalDeliveryEvidence> appended)
    {
        if (TryGetAppendedItems(current, next, out appended))
        {
            return true;
        }

        appended = [];
        if (current.Count < ScheduleContractLimits.RetainedTerminalDeliveryEvidenceItems
            || next.Count == 0
            || next.Count > current.Count)
        {
            return false;
        }

        var droppedCount = current.Count - next.Count + 1;
        if (droppedCount <= 0
            || !current.Skip(droppedCount).SequenceEqual(next.Take(next.Count - 1)))
        {
            return false;
        }

        appended = [next[^1]];
        return true;
    }

    private static void AddComposition(
        ICollection<ScheduleContractError> errors,
        string prefix,
        ScheduleContractValidationResult validation)
    {
        foreach (var error in validation.Errors)
        {
            errors.Add(new ScheduleContractError(error.Code, $"{prefix}{error.Path.TrimStart('$')}"));
        }
    }

    private static void Add(ICollection<ScheduleContractError> errors, string code, string path)
        => errors.Add(new ScheduleContractError(code, path));

    private static ScheduleContractValidationResult Result(IEnumerable<ScheduleContractError> errors)
        => new(errors);
}
