using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Captures one immutable, optimistic, crash-safe schedule-state version.</summary>
/// <remarks>The next occurrence remains unchanged while it is pending. Disablement prevents new claims but does not erase next-occurrence or misfire history.</remarks>
public sealed record ScheduleState
{
    private IReadOnlyList<ScheduleOccurrenceDispositionEvidence>? _dispositionEvidence;
    private IReadOnlyList<ScheduleTerminalDeliveryEvidence>? _terminalDeliveryEvidence;

    /// <summary>Initializes one state snapshot while defensively copying and canonically ordering evidence.</summary>
    public ScheduleState(
        int schemaVersion,
        ScheduleId scheduleId,
        long definitionRevision,
        string definitionHash,
        long stateRevision,
        bool enabled,
        ScheduleOccurrence? nextOccurrence,
        ScheduleCatchUpEpisode? catchUpEpisode,
        ScheduleDeferredOccurrence? deferredOccurrence,
        DateTimeOffset? lastClockObservedAtUtc,
        SchedulePendingDelivery? pendingDelivery,
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence>? dispositionEvidence,
        IReadOnlyList<ScheduleTerminalDeliveryEvidence>? terminalDeliveryEvidence)
    {
        SchemaVersion = schemaVersion;
        ScheduleId = scheduleId;
        DefinitionRevision = definitionRevision;
        DefinitionHash = definitionHash;
        StateRevision = stateRevision;
        Enabled = enabled;
        NextOccurrence = nextOccurrence;
        CatchUpEpisode = catchUpEpisode;
        DeferredOccurrence = deferredOccurrence;
        LastClockObservedAtUtc = lastClockObservedAtUtc;
        PendingDelivery = pendingDelivery;
        DispositionEvidence = dispositionEvidence!;
        TerminalDeliveryEvidence = terminalDeliveryEvidence!;
    }

    /// <summary>Gets the only supported state schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;

    /// <summary>Gets the exact state schema version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Gets the stable schedule identity.</summary>
    public ScheduleId ScheduleId { get; init; }

    /// <summary>Gets the exact pinned definition revision.</summary>
    public long DefinitionRevision { get; init; }

    /// <summary>Gets the lowercase SHA-256 hash of the exact pinned definition.</summary>
    public string DefinitionHash { get; init; }

    /// <summary>Gets the positive optimistic state revision.</summary>
    public long StateRevision { get; init; }

    /// <summary>Gets whether this state permits a new due-occurrence claim.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the exact next local and UTC occurrence, or null after exhaustion.</summary>
    public ScheduleOccurrence? NextOccurrence { get; init; }

    /// <summary>Gets the frozen bounded catch-up episode, if one is active.</summary>
    public ScheduleCatchUpEpisode? CatchUpEpisode { get; init; }

    /// <summary>Gets the exact next occurrence durably deferred by overlap, if any.</summary>
    public ScheduleDeferredOccurrence? DeferredOccurrence { get; init; }

    /// <summary>Gets the last persisted UTC wall-clock observation used for rollback detection.</summary>
    public DateTimeOffset? LastClockObservedAtUtc { get; init; }

    /// <summary>Gets the pending delivery persisted before queue admission.</summary>
    public SchedulePendingDelivery? PendingDelivery { get; init; }

    /// <summary>Gets a defensive, canonically ordered snapshot of bounded skipped/deferred evidence.</summary>
    public IReadOnlyList<ScheduleOccurrenceDispositionEvidence> DispositionEvidence
    {
        get => _dispositionEvidence!;
        init => _dispositionEvidence = ScheduleCollectionSnapshot.CopyAndOrder(
            value,
            ScheduleContractLimits.MaxDispositionEvidenceItems,
            Comparer<ScheduleOccurrenceDispositionEvidence>.Create(ScheduleEvidenceOrdering.Compare));
    }

    /// <summary>Gets a defensive, canonically ordered snapshot of finalized delivery results.</summary>
    public IReadOnlyList<ScheduleTerminalDeliveryEvidence> TerminalDeliveryEvidence
    {
        get => _terminalDeliveryEvidence!;
        init => _terminalDeliveryEvidence = ScheduleCollectionSnapshot.CopyAndOrder(
            value,
            ScheduleContractLimits.MaxTerminalDeliveryEvidenceItems,
            Comparer<ScheduleTerminalDeliveryEvidence>.Create(ScheduleTerminalEvidenceOrdering.Compare));
    }
}
