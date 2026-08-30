namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies the immutable fail-closed reason recorded with a non-approval decision-action retirement.</summary>
public enum HumanReviewDecisionActionRetirementReason
{
    /// <summary>No supported retirement reason was supplied.</summary>
    Unknown = 0,
    /// <summary>The action wake expired before the action completed.</summary>
    Expired = 1,
    /// <summary>Canonical action evidence was corrupt or contradictory.</summary>
    Invalid = 2,
    /// <summary>The action adapter returned conclusive invalid evidence.</summary>
    ReleaseInvalid = 3,
    /// <summary>The bounded claim history was exhausted after its latest claim strictly expired.</summary>
    ClaimLimitExceeded = 4,
}
