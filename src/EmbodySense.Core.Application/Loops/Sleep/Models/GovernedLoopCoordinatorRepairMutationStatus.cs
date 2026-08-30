namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Identifies one closed append-only coordinator-repair disposition outcome.</summary>
public enum GovernedLoopCoordinatorRepairMutationStatus
{
    /// <summary>The new exact disposition was appended.</summary>
    Appended = 1,

    /// <summary>The exact immutable disposition was already retained.</summary>
    Duplicate = 2,

    /// <summary>Current coordinator evidence no longer matches the failed generation.</summary>
    Conflict = 3,

    /// <summary>Retained or proposed evidence was malformed or contradictory.</summary>
    Corrupt = 4,

    /// <summary>The coordinator ledger could not be safely read or mutated.</summary>
    Unavailable = 5
}
