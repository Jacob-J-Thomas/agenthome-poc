namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies the result of the atomic reconciliation case and optional effect-head compare-exchange.</summary>
public enum GovernedLoopEffectReconciliationCaseMutationStatus
{
    /// <summary>No supported status was established.</summary>
    Unknown = 0,

    /// <summary>The immutable replacement and optional effect successor were atomically applied.</summary>
    Applied = 1,

    /// <summary>The exact operation and request hash already committed the same immutable outcome.</summary>
    Replayed = 2,

    /// <summary>The expected immutable case reference was stale or named different content.</summary>
    Conflict = 3,

    /// <summary>The compare-exchange request was malformed.</summary>
    Invalid = 4,

    /// <summary>The canonical case or effect-attempt artifact failed integrity validation.</summary>
    Corrupt = 5,

    /// <summary>The compare-exchange outcome could not be established conclusively.</summary>
    Unavailable = 6,

    /// <summary>The deterministic finite case or artifact capacity cannot admit this mutation; retrying the unchanged request cannot succeed.</summary>
    CapacityExceeded = 7,

    /// <summary>Interrupted atomic intent requires explicit store repair before another mutation.</summary>
    RepairRequired = 8,
}
