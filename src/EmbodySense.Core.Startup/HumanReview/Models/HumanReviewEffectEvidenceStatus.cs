namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Classifies the detached current effect posture without exposing Application-owned release contracts.</summary>
public enum HumanReviewEffectEvidenceStatus
{
    /// <summary>The source result is malformed or unsupported.</summary>
    Invalid = 0,

    /// <summary>The exact effect remains prepared and has not crossed dispatch.</summary>
    ExactNotStarted = 1,

    /// <summary>The exact effect crossed dispatch without a terminal outcome.</summary>
    Dispatched = 2,

    /// <summary>The exact effect has a conclusive nonterminal outcome.</summary>
    Conclusive = 3,

    /// <summary>The exact effect is ambiguous or conflicting.</summary>
    Ambiguous = 4,

    /// <summary>The exact effect is terminal and cannot be released again.</summary>
    Terminal = 5,

    /// <summary>No exact effect attempt was retained.</summary>
    Missing = 6,

    /// <summary>Retained evidence is corrupt, malformed, or unsupported.</summary>
    Corrupt = 7,

    /// <summary>The canonical source is unavailable.</summary>
    Unavailable = 8,

    /// <summary>Retained evidence drifted from the exact review binding.</summary>
    Stale = 9,
}
