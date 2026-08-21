namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Ranks attention deterministically without replacing the authoritative subsystem state.</summary>
public enum GovernedLoopPostureSeverity
{
    /// <summary>Normal informational posture.</summary>
    Information = 1,

    /// <summary>A nonterminal item merits attention but remains operable.</summary>
    Attention = 2,

    /// <summary>A bounded failure or stale condition blocks ordinary progress.</summary>
    Warning = 3,

    /// <summary>Corrupt, ambiguous, or authority-critical evidence requires explicit intervention.</summary>
    Critical = 4
}
