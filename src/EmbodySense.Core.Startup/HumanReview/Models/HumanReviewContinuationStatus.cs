namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Identifies the detached continuation posture retained for an approved Human Review.</summary>
public enum HumanReviewContinuationStatus
{
    /// <summary>No approval reservation exists.</summary>
    NotReserved = 0,
    /// <summary>An approval reservation exists but no wake was published.</summary>
    Reserved = 1,
    /// <summary>A wake was published and is awaiting a worker claim.</summary>
    Published = 2,
    /// <summary>A worker claim is retained.</summary>
    Claimed = 3,
    /// <summary>The continuation completed.</summary>
    Completed = 4,
    /// <summary>The continuation was retired without release.</summary>
    Retired = 5,
    /// <summary>Continuation evidence is malformed or inconsistent.</summary>
    Corrupt = 6
}
