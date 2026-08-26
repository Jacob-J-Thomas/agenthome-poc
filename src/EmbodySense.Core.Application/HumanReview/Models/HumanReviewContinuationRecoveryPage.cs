namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns one bounded canonical continuation discovery page and its opaque exclusive scan posture.</summary>
/// <param name="Status">The closed discovery posture.</param>
/// <param name="Candidates">The eligible candidates found while scanning this source page; the page may be underfilled when ineligible runs were filtered.</param>
/// <param name="NextScanCursor">The opaque cursor for the next source scan, or null only after an actual empty clean-tail probe.</param>
/// <param name="SourceTruncated">Whether the underlying canonical source retained more summaries beyond this page.</param>
public sealed record HumanReviewContinuationRecoveryPage(
    HumanReviewContinuationRecoveryPageStatus Status,
    IReadOnlyList<HumanReviewContinuationRecoveryCandidate> Candidates,
    string? NextScanCursor,
    bool SourceTruncated);
