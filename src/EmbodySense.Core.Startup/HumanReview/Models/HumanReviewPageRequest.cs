namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Specifies one finite Human Review list page without carrying workspace or authority data.</summary>
/// <param name="MaximumCount">The maximum number of canonical run summaries to inspect.</param>
/// <param name="Cursor">The opaque canonical run-store continuation cursor.</param>
public sealed record HumanReviewPageRequest(int MaximumCount = 50, string? Cursor = null);
