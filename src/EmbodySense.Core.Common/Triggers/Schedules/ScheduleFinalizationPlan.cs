using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Freezes the exact successor chronology to apply after a pending delivery reaches a terminal outcome.</summary>
/// <remarks>Recovery applies this plan and never recalculates recurrence after queue admission.</remarks>
public sealed record ScheduleFinalizationPlan
{
    private IReadOnlyList<ScheduleOccurrenceDispositionEvidence>? _dispositionEvidence;

    /// <summary>Initializes one immutable, canonically ordered finalization plan.</summary>
    public ScheduleFinalizationPlan(
        int schemaVersion,
        ScheduleOccurrence? nextOccurrence,
        ScheduleCatchUpEpisode? catchUpEpisode,
        ScheduleDeferredOccurrence? deferredOccurrence,
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence>? dispositionEvidence)
    {
        SchemaVersion = schemaVersion;
        NextOccurrence = nextOccurrence;
        CatchUpEpisode = catchUpEpisode;
        DeferredOccurrence = deferredOccurrence;
        DispositionEvidence = dispositionEvidence!;
    }

    /// <summary>Gets the only supported plan schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;

    /// <summary>Gets the exact plan schema version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Gets the exact successor occurrence, or null when the definition is exhausted.</summary>
    public ScheduleOccurrence? NextOccurrence { get; init; }

    /// <summary>Gets the remaining immutable catch-up episode after this pending occurrence finalizes.</summary>
    public ScheduleCatchUpEpisode? CatchUpEpisode { get; init; }

    /// <summary>Gets an exact successor occurrence already deferred by overlap, when applicable.</summary>
    public ScheduleDeferredOccurrence? DeferredOccurrence { get; init; }

    /// <summary>Gets skipped/deferred evidence to append atomically with successor activation.</summary>
    public IReadOnlyList<ScheduleOccurrenceDispositionEvidence> DispositionEvidence
    {
        get => _dispositionEvidence!;
        init => _dispositionEvidence = ScheduleCollectionSnapshot.CopyAndOrder(
            value,
            ScheduleContractLimits.MaxFinalizationEvidenceItems,
            Comparer<ScheduleOccurrenceDispositionEvidence>.Create(ScheduleEvidenceOrdering.Compare));
    }
}
