namespace EmbodySense.Core.Persistence.Triggers.Schedules.Models;

/// <summary>Identifies schedule-catalog durability boundaries exposed for crash and restart verification.</summary>
public enum ScheduleStorePersistenceBoundary
{
    /// <summary>The catalog generation has not reached a named durability boundary.</summary>
    Unknown = 0,
    /// <summary>An empty create-new precursor is open under retained directory authority.</summary>
    PrecursorCreated = 1,
    /// <summary>The complete candidate generation is flushed but not yet published.</summary>
    Staged = 2,
    /// <summary>The flushed generation is about to cross the atomic no-replace publication boundary.</summary>
    Publishing = 3,
    /// <summary>The immutable generation is durably published and is authoritative after restart.</summary>
    Published = 4,
}
