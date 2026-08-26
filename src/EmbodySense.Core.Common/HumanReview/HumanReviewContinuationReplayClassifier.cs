using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Distinguishes an idempotent exact artifact replay from unsafe divergent identity reuse without changing state.</summary>
public static class HumanReviewContinuationReplayClassifier
{
    /// <summary>Classifies proposed wake identity reuse against one retained wake.</summary>
    public static HumanReviewContinuationReplayDisposition ClassifyWake(HumanReviewContinuationWake? retained, HumanReviewContinuationWake? proposed)
    {
        if (retained is null || proposed is null || !string.Equals(retained.WakeId, proposed.WakeId, StringComparison.Ordinal)) return HumanReviewContinuationReplayDisposition.New;
        return string.Equals(retained.WakeHash, proposed.WakeHash, StringComparison.Ordinal) && Equals(retained, proposed) ? HumanReviewContinuationReplayDisposition.ExactReplay : HumanReviewContinuationReplayDisposition.DivergentReuse;
    }

    /// <summary>Classifies proposed claim identity reuse against one retained claim.</summary>
    public static HumanReviewContinuationReplayDisposition ClassifyClaim(HumanReviewContinuationClaim? retained, HumanReviewContinuationClaim? proposed)
    {
        if (retained is null || proposed is null || !string.Equals(retained.ClaimId, proposed.ClaimId, StringComparison.Ordinal)) return HumanReviewContinuationReplayDisposition.New;
        return string.Equals(retained.ClaimHash, proposed.ClaimHash, StringComparison.Ordinal) && Equals(retained, proposed) ? HumanReviewContinuationReplayDisposition.ExactReplay : HumanReviewContinuationReplayDisposition.DivergentReuse;
    }
}
