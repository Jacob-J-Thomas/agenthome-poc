namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies whether an effect attempt is conclusively safe for Human Review approval.</summary>
public enum HumanReviewEffectDispatchCertainty
{
    /// <summary>No supported certainty posture was supplied.</summary>
    Unknown = 0,
    /// <summary>The effect intent is prepared and evidence conclusively proves no irreversible dispatch boundary was crossed.</summary>
    NotDispatched = 1,
    /// <summary>Evidence proves the irreversible dispatch boundary was crossed.</summary>
    Dispatched = 2,
    /// <summary>Evidence is missing, conflicting, or otherwise cannot prove whether dispatch occurred.</summary>
    Ambiguous = 3
}
