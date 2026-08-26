namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Classifies a proposed artifact reuse without accepting it or mutating durable state.</summary>
public enum HumanReviewContinuationReplayDisposition
{
    /// <summary>The proposed artifact has a new identity.</summary>
    New = 1,
    /// <summary>The proposed artifact has the same identity and exact canonical hash as the retained artifact.</summary>
    ExactReplay = 2,
    /// <summary>The proposed artifact reuses an identity but diverges from the retained exact canonical artifact.</summary>
    DivergentReuse = 3
}
