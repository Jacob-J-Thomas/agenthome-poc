namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns the closed result of one bounded host-neutral continuation recovery pass.</summary>
/// <param name="Status">The pass posture.</param>
/// <param name="NextScanCursor">The opaque cursor for the next pass, or null only after a clean empty-tail probe.</param>
/// <param name="SourceTruncated">Whether the source page itself retained more summaries.</param>
/// <param name="Items">One outcome per eligible discovered candidate.</param>
public sealed record HumanReviewContinuationRecoveryResult(
    HumanReviewContinuationRecoveryStatus Status,
    string? NextScanCursor,
    bool SourceTruncated,
    IReadOnlyList<HumanReviewContinuationRecoveryItemResult> Items);
