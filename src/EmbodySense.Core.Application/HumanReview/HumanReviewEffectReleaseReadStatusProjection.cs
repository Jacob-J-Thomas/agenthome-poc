using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Projects read-only effect-certainty source results into fail-closed Human Review release postures.</summary>
public static class HumanReviewEffectReleaseReadStatusProjection
{
    /// <summary>Projects one query-bound source result without calling adapters, changing state, releasing a continuation, or treating approval as authority.</summary>
    /// <param name="query">The exact immutable effect identity and preparation expected by the Human Review continuation.</param>
    /// <param name="result">The detached read-only source outcome to project.</param>
    /// <returns>A closed fail-closed release posture; no return value grants dispatch authority.</returns>
    public static HumanReviewEffectReleaseReadStatus Project(GovernedLoopEffectCertaintySnapshotQuery? query, GovernedLoopEffectCertaintySnapshotResult? result)
    {
        if (query is null
            || !HumanReviewEffectReleaseContract.TryCaptureExpectation(query.Identity, query.Preparation, out var expectedIdentity, out var expectedPreparation, out _)
            || expectedIdentity is null
            || expectedPreparation is null
            || result is null
            || !Enum.IsDefined(result.Status))
        {
            return HumanReviewEffectReleaseReadStatus.Invalid;
        }

        return result.Status switch
        {
            GovernedLoopEffectCertaintySnapshotStatus.Missing when result.Snapshot is null => HumanReviewEffectReleaseReadStatus.Missing,
            GovernedLoopEffectCertaintySnapshotStatus.Corrupt when result.Snapshot is null => HumanReviewEffectReleaseReadStatus.Corrupt,
            GovernedLoopEffectCertaintySnapshotStatus.Unavailable when result.Snapshot is null => HumanReviewEffectReleaseReadStatus.Unavailable,
            GovernedLoopEffectCertaintySnapshotStatus.Stale when result.Snapshot is null => HumanReviewEffectReleaseReadStatus.Stale,
            GovernedLoopEffectCertaintySnapshotStatus.Current => ProjectCurrent(expectedIdentity, expectedPreparation, result.Snapshot),
            _ => HumanReviewEffectReleaseReadStatus.Invalid,
        };
    }

    private static HumanReviewEffectReleaseReadStatus ProjectCurrent(
        HumanReviewEffectAttemptIdentity expectedIdentity,
        HumanReviewEffectPreparationFingerprint expectedPreparation,
        HumanReviewEffectCertaintySnapshot? snapshot)
    {
        if (HumanReviewEffectReleaseContract.TryCapture(snapshot, out var detached, out _) is false || detached is null)
        {
            return HumanReviewEffectReleaseReadStatus.Invalid;
        }
        if (!Equals(expectedIdentity, detached.Identity) || !Equals(expectedPreparation, detached.Preparation))
        {
            return HumanReviewEffectReleaseReadStatus.Stale;
        }

        if (detached.Certainty == HumanReviewEffectCertainty.NotStarted
            && (detached.Phase != GovernedLoopEffectPhase.IntentPrepared || detached.DispatchAuthorityEvidenceHash is not null))
        {
            return HumanReviewEffectReleaseReadStatus.Stale;
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
