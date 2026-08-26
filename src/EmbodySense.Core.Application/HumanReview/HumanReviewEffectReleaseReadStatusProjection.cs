using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Projects read-only effect-certainty source results into fail-closed Human Review release postures.</summary>
public static class HumanReviewEffectReleaseReadStatusProjection
{
    /// <summary>Projects one source result without calling adapters, changing state, releasing a continuation, or treating approval as authority.</summary>
    public static HumanReviewEffectReleaseReadStatus Project(GovernedLoopEffectCertaintySnapshotResult? result)
    {
        if (result is null || !Enum.IsDefined(result.Status))
        {
            return HumanReviewEffectReleaseReadStatus.Invalid;
        }

        return result.Status switch
        {
            GovernedLoopEffectCertaintySnapshotStatus.Missing when result.Snapshot is null => HumanReviewEffectReleaseReadStatus.Missing,
            GovernedLoopEffectCertaintySnapshotStatus.Corrupt when result.Snapshot is null => HumanReviewEffectReleaseReadStatus.Corrupt,
            GovernedLoopEffectCertaintySnapshotStatus.Unavailable when result.Snapshot is null => HumanReviewEffectReleaseReadStatus.Unavailable,
            GovernedLoopEffectCertaintySnapshotStatus.Stale when result.Snapshot is null => HumanReviewEffectReleaseReadStatus.Stale,
            GovernedLoopEffectCertaintySnapshotStatus.Current => ProjectCurrent(result.Snapshot),
            _ => HumanReviewEffectReleaseReadStatus.Invalid,
        };
    }

    private static HumanReviewEffectReleaseReadStatus ProjectCurrent(HumanReviewEffectCertaintySnapshot? snapshot)
    {
        if (HumanReviewEffectReleaseContract.TryCapture(snapshot, out var detached, out _) is false || detached is null)
        {
            return HumanReviewEffectReleaseReadStatus.Invalid;
        }

        return detached.Certainty switch
        {
            HumanReviewEffectCertainty.NotStarted => HumanReviewEffectReleaseReadStatus.ExactNotStarted,
            HumanReviewEffectCertainty.Dispatched => HumanReviewEffectReleaseReadStatus.Dispatched,
            HumanReviewEffectCertainty.Conclusive => HumanReviewEffectReleaseReadStatus.Conclusive,
            HumanReviewEffectCertainty.Ambiguous => HumanReviewEffectReleaseReadStatus.Ambiguous,
            HumanReviewEffectCertainty.Terminal => HumanReviewEffectReleaseReadStatus.Terminal,
            _ => HumanReviewEffectReleaseReadStatus.Invalid,
        };
    }
}
