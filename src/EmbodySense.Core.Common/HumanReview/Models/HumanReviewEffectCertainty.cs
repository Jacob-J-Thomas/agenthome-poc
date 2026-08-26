namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Classifies the only safe current postures for a reviewed effect attempt.</summary>
public enum HumanReviewEffectCertainty
{
    /// <summary>No supported, fail-closed certainty posture was supplied.</summary>
    Unknown = 0,

    /// <summary>Evidence proves the exact attempt has not crossed its irreversible dispatch boundary.</summary>
    NotStarted = 1,

    /// <summary>Evidence proves the irreversible dispatch boundary was crossed but no conclusive outcome is retained.</summary>
    Dispatched = 2,

    /// <summary>A conclusive outcome is retained but the attempt is not yet terminally committed.</summary>
    Conclusive = 3,

    /// <summary>Evidence is ambiguous or conflicting and must never authorize redispatch.</summary>
    Ambiguous = 4,

    /// <summary>The attempt has a terminal committed or reconciled posture and must never be released again.</summary>
    Terminal = 5,
}
