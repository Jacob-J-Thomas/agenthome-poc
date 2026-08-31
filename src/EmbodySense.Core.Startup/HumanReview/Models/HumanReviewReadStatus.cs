namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Identifies the closed outcome of one exact Human Review read.</summary>
public enum HumanReviewReadStatus
{
    /// <summary>No supported outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact review was found and projected.</summary>
    Ready = 1,
    /// <summary>The exact run or review was not retained.</summary>
    NotFound = 2,
    /// <summary>The retained review artifact failed strict validation.</summary>
    Corrupt = 3,
    /// <summary>The canonical run store was unavailable.</summary>
    Unavailable = 4,
    /// <summary>The read identity was malformed.</summary>
    Invalid = 5
}
