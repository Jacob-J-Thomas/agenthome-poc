namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Identifies one coordinator-repair disposition read outcome.</summary>
public enum GovernedLoopCoordinatorRepairReadStatus
{
    /// <summary>One exact latest repair disposition was found.</summary>
    Found = 1,

    /// <summary>No matching repair disposition exists.</summary>
    NotFound = 2,

    /// <summary>Retained repair evidence was malformed or contradictory.</summary>
    Corrupt = 3,

    /// <summary>The coordinator ledger was unavailable.</summary>
    Unavailable = 4
}
