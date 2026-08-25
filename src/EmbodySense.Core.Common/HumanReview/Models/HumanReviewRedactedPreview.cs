namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Retains one bounded, redacted, display-safe preview that cannot carry raw executable inputs, credentials, or private payloads.</summary>
/// <param name="Kind">The semantic display role of the preview.</param>
/// <param name="Label">The bounded display-safe label.</param>
/// <param name="Detail">The bounded display-safe redacted detail.</param>
/// <param name="DetailHash">The canonical hash of the exact kind, label, and redacted detail.</param>
public sealed partial record HumanReviewRedactedPreview(HumanReviewPreviewKind Kind, string Label, string Detail, string DetailHash);
