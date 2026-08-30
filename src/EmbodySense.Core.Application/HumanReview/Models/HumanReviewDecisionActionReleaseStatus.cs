namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Describes the host-owned action adapter result.</summary>
public enum HumanReviewDecisionActionReleaseStatus
{
    /// <summary>No supported result was supplied.</summary>
    Unknown = 0,
    /// <summary>The action completed with conclusive durable evidence.</summary>
    Completed = 1,
    /// <summary>The action evidence is invalid or contradictory.</summary>
    Invalid = 2,
    /// <summary>The host result is ambiguous or unavailable.</summary>
    Unavailable = 3,
}
