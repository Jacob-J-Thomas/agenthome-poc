namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Classifies the detached dispatch posture of one reviewed effect attempt.</summary>
public enum HumanReviewEffectCertainty
{
    /// <summary>No supported fail-closed certainty posture was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact attempt has not crossed its irreversible dispatch boundary.</summary>
    NotStarted = 1,
    /// <summary>The irreversible boundary was crossed without a conclusive outcome.</summary>
    Dispatched = 2,
    /// <summary>A conclusive outcome is retained but the attempt is not terminal.</summary>
    Conclusive = 3,
    /// <summary>Evidence is ambiguous or conflicting and cannot authorize redispatch.</summary>
    Ambiguous = 4,
    /// <summary>The attempt is terminal and cannot be released again.</summary>
    Terminal = 5
}
