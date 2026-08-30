namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Identifies the bounded fail-closed reason for action retirement.</summary>
public enum HumanReviewDecisionActionRetirementReason
{
    /// <summary>No supported retirement reason was supplied.</summary>
    Unknown = 0,
    /// <summary>The action wake expired before a valid claim could conclude.</summary>
    Expired = 1,
    /// <summary>Canonical action evidence was corrupt or contradictory.</summary>
    Invalid = 2,
    /// <summary>The action adapter returned conclusive invalid evidence.</summary>
    ReleaseInvalid = 3,
}
