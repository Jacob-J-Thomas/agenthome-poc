namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Describes the closed result of one bounded non-approval action discovery page.</summary>
public enum HumanReviewDecisionActionRecoveryPageStatus
{
    /// <summary>No supported result was supplied.</summary>
    Unknown = 0,
    /// <summary>The canonical page is current.</summary>
    Current = 1,
    /// <summary>The request was malformed or canonical content was corrupt.</summary>
    Invalid = 2,
    /// <summary>The canonical source was unavailable.</summary>
    Unavailable = 3,
}
