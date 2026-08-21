namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;

/// <summary>Identifies exact sleep-ledger durability boundaries exposed for crash and restart verification.</summary>
public enum GovernedLoopSleepStorePersistenceBoundary
{
    /// <summary>No named durability boundary has been reached.</summary>
    Unknown = 0,

    /// <summary>An empty create-new precursor exists under retained directory authority.</summary>
    PrecursorCreated = 1,

    /// <summary>The complete candidate ledger is flushed but not yet published.</summary>
    Staged = 2,

    /// <summary>The ledger is about to cross the atomic no-replace publication boundary.</summary>
    Publishing = 3,

    /// <summary>The immutable generation is durably published and authoritative after restart.</summary>
    Published = 4
}
