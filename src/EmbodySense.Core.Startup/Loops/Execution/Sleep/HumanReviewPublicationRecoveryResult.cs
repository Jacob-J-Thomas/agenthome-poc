namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Returns the bounded wake-less approval discovery posture and its opaque scan cursor.</summary>
/// <param name="Status">The canonical publication scan posture.</param>
/// <param name="NextScanCursor">The opaque canonical run-page cursor for the next publication scan.</param>
/// <param name="SourceTruncated">Whether the canonical source retained summaries beyond this page.</param>
/// <param name="Items">The bounded publication outcomes for candidates processed by this pass.</param>
public sealed record HumanReviewPublicationRecoveryResult(
    HumanReviewPublicationRecoveryStatus Status,
    string? NextScanCursor,
    bool SourceTruncated,
    IReadOnlyList<HumanReviewPublicationRecoveryItemResult> Items);
