namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Distinguishes a safe exact certainty-snapshot replay from unsafe identity reuse.</summary>
public enum HumanReviewEffectSnapshotReplayDisposition
{
    /// <summary>The snapshots name different exact effect attempts.</summary>
    New = 0,

    /// <summary>The snapshots are exact canonical replays of the same reading.</summary>
    ExactReplay = 1,

    /// <summary>The snapshots reuse an identity but diverge in preparation, certainty, authority, or retained evidence.</summary>
    DivergentReuse = 2,
}
