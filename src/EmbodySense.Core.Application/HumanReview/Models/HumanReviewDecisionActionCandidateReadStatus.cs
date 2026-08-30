namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Describes the closed result of a fenced reread of one non-approval decision action.</summary>
public enum HumanReviewDecisionActionCandidateReadStatus
{
    /// <summary>No supported result was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact claimed candidate is current.</summary>
    Current = 1,
    /// <summary>The named run was absent.</summary>
    Missing = 2,
    /// <summary>The requested exact action binding was superseded or diverged.</summary>
    Stale = 3,
    /// <summary>The retained artifact was malformed or contradictory.</summary>
    Corrupt = 4,
    /// <summary>The canonical source was unavailable or ambiguous.</summary>
    Unavailable = 5,
}
