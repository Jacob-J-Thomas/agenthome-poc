namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Projects one contract-validated redacted review preview for display.</summary>
/// <param name="Kind">The semantic preview kind.</param>
/// <param name="Label">The bounded display label.</param>
/// <param name="Detail">The bounded display-safe detail.</param>
/// <param name="DetailHash">The canonical preview detail hash.</param>
public sealed record HumanReviewPreview(HumanReviewPreviewKind Kind, string Label, string Detail, string DetailHash);
