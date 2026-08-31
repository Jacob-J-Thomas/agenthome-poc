namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Identifies the closed outcome of one bounded Human Review list operation.</summary>
public enum HumanReviewPageStatus
{
    /// <summary>No supported outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>The page was read and projected successfully.</summary>
    Ready = 1,
    /// <summary>The page request or canonical page was malformed.</summary>
    Invalid = 2,
    /// <summary>The canonical run store was unavailable.</summary>
    Unavailable = 3,
    /// <summary>Canonical records could not be projected without ambiguity.</summary>
    Ambiguous = 4,
    /// <summary>A retained Human Review artifact failed strict validation.</summary>
    Corrupt = 5
}
