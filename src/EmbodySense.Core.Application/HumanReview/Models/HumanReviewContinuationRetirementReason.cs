namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Classifies the bounded fail-closed reason for retiring an approved continuation wake.</summary>
public enum HumanReviewContinuationRetirementReason
{
    /// <summary>No supported retirement reason was supplied.</summary>
    Unknown = 0,

    /// <summary>The wake or active claim expired under trusted time.</summary>
    Expired = 1,

    /// <summary>The run, frontier, revision, decision, reservation, or generation no longer matches the review binding.</summary>
    Superseded = 2,

    /// <summary>Current authority, effect certainty, or mandatory evidence blocks release.</summary>
    Blocked = 3,
}
