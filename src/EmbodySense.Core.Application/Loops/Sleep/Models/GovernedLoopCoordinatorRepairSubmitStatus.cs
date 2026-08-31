namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Identifies one closed authority-bound repair submission outcome.</summary>
public enum GovernedLoopCoordinatorRepairSubmitStatus
{
    /// <summary>The new repair disposition was appended.</summary>
    Accepted = 1,

    /// <summary>The exact retained repair disposition was replayed.</summary>
    Replayed = 2,

    /// <summary>The submitted preview binding was malformed.</summary>
    Invalid = 3,

    /// <summary>Current authenticated operator authority did not permit the repair.</summary>
    Unauthorized = 4,

    /// <summary>The previewed failed evidence, authority, or readiness no longer matches current state.</summary>
    Conflict = 5,

    /// <summary>Evidence was malformed or ambiguous.</summary>
    Corrupt = 6,

    /// <summary>An authority, coordinator, dependency, or ledger source was unavailable.</summary>
    Unavailable = 7
}
