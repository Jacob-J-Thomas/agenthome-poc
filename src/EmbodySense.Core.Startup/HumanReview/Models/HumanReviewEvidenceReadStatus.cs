namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Identifies the closed outcome of one detached Human Review evidence read.</summary>
public enum HumanReviewEvidenceReadStatus
{
    /// <summary>No supported outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>Canonical review evidence was read successfully.</summary>
    Ready = 1,
    /// <summary>The exact review was not retained.</summary>
    NotFound = 2,
    /// <summary>The retained evidence was malformed.</summary>
    Corrupt = 3,
    /// <summary>The canonical source was unavailable.</summary>
    Unavailable = 4,
    /// <summary>The read identity was malformed.</summary>
    Invalid = 5
}
