using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Distinguishes an idempotent exact artifact replay from unsafe divergent identity reuse without changing state.</summary>
public static class HumanReviewContinuationReplayClassifier
{
    /// <summary>Classifies proposed wake identity reuse against one retained wake.</summary>
    public static HumanReviewContinuationReplayDisposition ClassifyWake(HumanReviewContinuationWake? retained, HumanReviewContinuationWake? proposed)
    {
        if (retained is null || proposed is null || !string.Equals(retained.WakeId, proposed.WakeId, StringComparison.Ordinal)) return HumanReviewContinuationReplayDisposition.New;
        return Exact(retained.WakeHash, proposed.WakeHash, HumanReviewContinuationContractHash.MatchesWake(retained), HumanReviewContinuationContractHash.MatchesWake(proposed));
    }

    /// <summary>Classifies proposed claim identity reuse against one retained claim.</summary>
    public static HumanReviewContinuationReplayDisposition ClassifyClaim(HumanReviewContinuationClaim? retained, HumanReviewContinuationClaim? proposed)
    {
        if (retained is null || proposed is null || !string.Equals(retained.ClaimId, proposed.ClaimId, StringComparison.Ordinal)) return HumanReviewContinuationReplayDisposition.New;
        return Exact(retained.ClaimHash, proposed.ClaimHash, HumanReviewContinuationContractHash.MatchesClaim(retained), HumanReviewContinuationContractHash.MatchesClaim(proposed));
    }

    /// <summary>Classifies proposed completion identity reuse using canonical hashes rather than process-local record or collection identity.</summary>
    public static HumanReviewContinuationReplayDisposition ClassifyCompletion(HumanReviewContinuationCompletion? retained, HumanReviewContinuationCompletion? proposed)
    {
        if (retained is null || proposed is null || !string.Equals(retained.CompletionId, proposed.CompletionId, StringComparison.Ordinal)) return HumanReviewContinuationReplayDisposition.New;
        return Exact(retained.CompletionHash, proposed.CompletionHash, HumanReviewContinuationContractHash.MatchesCompletion(retained), HumanReviewContinuationContractHash.MatchesCompletion(proposed));
    }

    /// <summary>Classifies proposed retirement identity reuse using canonical hashes rather than process-local record or collection identity.</summary>
    public static HumanReviewContinuationReplayDisposition ClassifyRetirement(HumanReviewContinuationRetirement? retained, HumanReviewContinuationRetirement? proposed)
    {
        if (retained is null || proposed is null || !string.Equals(retained.RetirementId, proposed.RetirementId, StringComparison.Ordinal)) return HumanReviewContinuationReplayDisposition.New;
        return Exact(retained.RetirementHash, proposed.RetirementHash, HumanReviewContinuationContractHash.MatchesRetirement(retained), HumanReviewContinuationContractHash.MatchesRetirement(proposed));
    }

    /// <summary>Classifies whole-state replay by immutable wake identity and canonical state hash without depending on an <see cref="System.Collections.Immutable.ImmutableArray{T}"/> backing array.</summary>
    public static HumanReviewContinuationReplayDisposition ClassifyState(HumanReviewContinuationState? retained, HumanReviewContinuationState? proposed)
    {
        if (retained?.Wake is null || proposed?.Wake is null || !string.Equals(retained.Wake.WakeId, proposed.Wake.WakeId, StringComparison.Ordinal)) return HumanReviewContinuationReplayDisposition.New;
        return Exact(retained.StateHash, proposed.StateHash, HumanReviewContinuationContractHash.MatchesState(retained), HumanReviewContinuationContractHash.MatchesState(proposed));
    }

    private static HumanReviewContinuationReplayDisposition Exact(string retainedHash, string proposedHash, bool retainedMatches, bool proposedMatches)
        => retainedMatches && proposedMatches && string.Equals(retainedHash, proposedHash, StringComparison.Ordinal)
            ? HumanReviewContinuationReplayDisposition.ExactReplay
            : HumanReviewContinuationReplayDisposition.DivergentReuse;
}
