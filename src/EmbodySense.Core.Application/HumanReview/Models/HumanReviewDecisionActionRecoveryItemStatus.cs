namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Describes the closed result for one discovered decision action.</summary>
public enum HumanReviewDecisionActionRecoveryItemStatus
{
    /// <summary>No supported result was supplied.</summary>
    Unknown = 0,
    /// <summary>The action completed conclusively.</summary>
    Completed = 1,
    /// <summary>The action was retired fail-closed.</summary>
    Retired = 2,
    /// <summary>Another worker or lifecycle change won the claim.</summary>
    ClaimConflict = 3,
    /// <summary>The action became stale after claim.</summary>
    StaleAfterClaim = 4,
    /// <summary>Evidence was unavailable or ambiguous and remains recoverable.</summary>
    Parked = 5,
    /// <summary>The candidate was invalid or corrupt.</summary>
    Invalid = 6,
}
