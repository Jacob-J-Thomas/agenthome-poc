namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Identifies the closed posture of one bounded Human Review continuation recovery pass.</summary>
public enum HumanReviewContinuationRecoveryStatus
{
    /// <summary>No supported result was produced.</summary>
    Unknown = 0,

    /// <summary>The bounded pass completed and retained its next scan posture.</summary>
    Current = 1,

    /// <summary>The request, trusted time, or source page was invalid.</summary>
    Invalid = 2,

    /// <summary>A bounded dependency was unavailable; no additional candidate was dispatched.</summary>
    Unavailable = 3,
}
