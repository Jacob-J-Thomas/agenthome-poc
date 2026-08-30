namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Describes the closed result of one canonical non-approval action mutation.</summary>
public enum HumanReviewDecisionActionStoreMutationStatus
{
    /// <summary>No supported result was supplied.</summary>
    Unknown = 0,
    /// <summary>The mutation committed.</summary>
    Committed = 1,
    /// <summary>The exact mutation was already retained.</summary>
    Replayed = 2,
    /// <summary>Current state diverged.</summary>
    Conflict = 3,
    /// <summary>The run was absent.</summary>
    NotFound = 4,
    /// <summary>Request or canonical content was invalid.</summary>
    Invalid = 5,
    /// <summary>The run quota rejected the mutation.</summary>
    LimitExceeded = 6,
    /// <summary>The canonical source was unavailable.</summary>
    Unavailable = 7,
}
