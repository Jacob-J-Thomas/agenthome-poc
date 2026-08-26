namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Identifies the bounded recovery outcome for one exact candidate without implying a release occurred.</summary>
public enum HumanReviewContinuationRecoveryItemStatus
{
    /// <summary>No supported candidate outcome was produced.</summary>
    Unknown = 0,

    /// <summary>The exact claim committed and the release completion committed or replayed.</summary>
    Completed = 1,

    /// <summary>The exact claim committed and the fail-closed retirement committed or replayed.</summary>
    Retired = 2,

    /// <summary>The exact claim committed but a canonical reread no longer matched.</summary>
    StaleAfterClaim = 3,

    /// <summary>The candidate changed concurrently before this pass acquired its claim.</summary>
    ClaimConflict = 4,

    /// <summary>The exact claim had already been persisted by an earlier response-unknown attempt; this pass did not redispatch it.</summary>
    ClaimReplayed = 5,

    /// <summary>The candidate was unavailable or an action outcome remained ambiguous; no terminal mutation or redispatch occurred.</summary>
    Parked = 6,

    /// <summary>The candidate or release result was conclusively invalid and a retirement could not be recorded.</summary>
    Invalid = 7,

    /// <summary>The wake had already expired before a new claim could be valid, so it remains inspectable without synthetic ownership or mutation.</summary>
    ExpiredWakeRetained = 8,
}
