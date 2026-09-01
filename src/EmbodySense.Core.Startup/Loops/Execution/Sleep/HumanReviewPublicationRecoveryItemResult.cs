namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Returns a non-secret publication outcome for one canonical run.</summary>
/// <param name="RunId">The canonical run identity.</param>
/// <param name="Status">The bounded publication posture.</param>
public sealed record HumanReviewPublicationRecoveryItemResult(string RunId, HumanReviewPublicationRecoveryItemStatus Status);
