namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Projects a closed source result into the only release-relevant safe postures without granting release authority.</summary>
public enum HumanReviewEffectReleaseReadStatus
{
    /// <summary>The source result is malformed or not a supported closed disposition.</summary>
    Invalid = 0,

    /// <summary>The exact effect remains in its initial unauthorised IntentPrepared state; separate release-time authority checks are still mandatory.</summary>
    ExactNotStarted = 1,

    /// <summary>The exact effect has crossed dispatch without a terminal outcome and must not be redispatched.</summary>
    Dispatched = 2,

    /// <summary>The exact effect has a conclusive nonterminal outcome and must not be redispatched.</summary>
    Conclusive = 3,

    /// <summary>The exact effect is ambiguous or conflicting and must not be redispatched.</summary>
    Ambiguous = 4,

    /// <summary>The exact effect is terminal and must not be released again.</summary>
    Terminal = 5,

    /// <summary>No exact effect attempt was retained.</summary>
    Missing = 6,

    /// <summary>Retained evidence is corrupt, malformed, or unsupported.</summary>
    Corrupt = 7,

    /// <summary>The source is unavailable.</summary>
    Unavailable = 8,

    /// <summary>Current retained evidence drifted or is stale relative to the exact review binding.</summary>
    Stale = 9,
}
