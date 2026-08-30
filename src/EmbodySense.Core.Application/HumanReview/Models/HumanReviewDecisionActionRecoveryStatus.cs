namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Describes the closed result of a bounded non-approval action recovery pass.</summary>
public enum HumanReviewDecisionActionRecoveryStatus
{
    /// <summary>No supported result was supplied.</summary>
    Unknown = 0,
    /// <summary>The canonical page was processed.</summary>
    Current = 1,
    /// <summary>The request or retained source was invalid.</summary>
    Invalid = 2,
    /// <summary>The canonical source was unavailable.</summary>
    Unavailable = 3,
}
