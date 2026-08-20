using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Creates isolated copies of schedule contracts at public and persistence boundaries.</summary>
public static class ScheduleContractCopy
{
    /// <summary>Copies one definition without normalizing or validating it.</summary>
    public static ScheduleDefinition? Copy(ScheduleDefinition? definition)
        => definition is null
            ? null
            : definition with
            {
                AuthorityProfile = definition.AuthorityProfile is null ? null! : definition.AuthorityProfile with { },
                Recurrence = definition.Recurrence is null ? null! : definition.Recurrence with { },
                TimeZone = definition.TimeZone is null ? null! : definition.TimeZone with { },
                DaylightSaving = definition.DaylightSaving is null ? null! : definition.DaylightSaving with { },
                Misfire = definition.Misfire is null ? null! : definition.Misfire with { },
                Payload = definition.Payload is null ? null! : definition.Payload with { },
            };

    /// <summary>Copies one state and every schedule-owned nested record without validating it.</summary>
    public static ScheduleState? Copy(ScheduleState? state)
        => state is null
            ? null
            : new ScheduleState(
                state.SchemaVersion,
                state.ScheduleId,
                state.DefinitionRevision,
                state.DefinitionHash,
                state.StateRevision,
                state.Enabled,
                Copy(state.NextOccurrence),
                state.CatchUpEpisode is null ? null : state.CatchUpEpisode with { },
                Copy(state.DeferredOccurrence),
                state.LastClockObservedAtUtc,
                Copy(state.PendingDelivery),
                state.DispositionEvidence?.Select(Copy).ToArray()!,
                state.TerminalDeliveryEvidence?.Select(Copy).ToArray()!);

    /// <summary>Copies one exact occurrence.</summary>
    public static ScheduleOccurrence? Copy(ScheduleOccurrence? occurrence)
        => occurrence is null
            ? null
            : occurrence with { TimeZone = occurrence.TimeZone is null ? null! : occurrence.TimeZone with { } };

    /// <summary>Copies one pending delivery and optional result evidence.</summary>
    public static SchedulePendingDelivery? Copy(SchedulePendingDelivery? pending)
        => pending is null
            ? null
            : pending with
            {
                Occurrence = Copy(pending.Occurrence)!,
                Identity = pending.Identity is null ? null! : pending.Identity with { },
                FinalizationPlan = Copy(pending.FinalizationPlan),
                Prepared = pending.Prepared is null ? null : pending.Prepared with { },
                Result = pending.Result is null ? null : pending.Result with { },
            };

    /// <summary>Copies one disposition evidence item.</summary>
    public static ScheduleOccurrenceDispositionEvidence Copy(ScheduleOccurrenceDispositionEvidence evidence)
        => evidence is null
            ? null!
            : evidence with { TimeZone = evidence.TimeZone is null ? null! : evidence.TimeZone with { } };

    /// <summary>Copies one immutable finalization plan and its nested evidence.</summary>
    public static ScheduleFinalizationPlan? Copy(ScheduleFinalizationPlan? plan)
        => plan is null
            ? null
            : new ScheduleFinalizationPlan(
                plan.SchemaVersion,
                Copy(plan.NextOccurrence),
                plan.CatchUpEpisode is null ? null : plan.CatchUpEpisode with { },
                Copy(plan.DeferredOccurrence),
                plan.DispositionEvidence?.Select(Copy).ToArray()!);

    /// <summary>Copies one explicit overlap deferral.</summary>
    public static ScheduleDeferredOccurrence? Copy(ScheduleDeferredOccurrence? deferred)
        => deferred is null
            ? null
            : deferred with
            {
                Occurrence = Copy(deferred.Occurrence)!,
                Identity = deferred.Identity is null ? null! : deferred.Identity with { },
            };

    /// <summary>Copies one finalized delivery-evidence item.</summary>
    public static ScheduleTerminalDeliveryEvidence Copy(ScheduleTerminalDeliveryEvidence evidence)
        => evidence is null
            ? null!
            : evidence with
            {
                Occurrence = Copy(evidence.Occurrence)!,
                Identity = evidence.Identity is null ? null! : evidence.Identity with { },
                Result = evidence.Result is null ? null! : evidence.Result with { },
            };

    /// <summary>Copies one derived delivery-provenance projection and all schedule-owned nested records.</summary>
    public static ScheduleDeliveryProvenanceEvidence? Copy(ScheduleDeliveryProvenanceEvidence? evidence)
        => evidence is null
            ? null
            : evidence with
            {
                Definition = Copy(evidence.Definition)!,
                Occurrence = Copy(evidence.Occurrence)!,
                Identity = evidence.Identity is null ? null! : evidence.Identity with { },
                Result = evidence.Result is null ? null! : evidence.Result with { },
            };
}
