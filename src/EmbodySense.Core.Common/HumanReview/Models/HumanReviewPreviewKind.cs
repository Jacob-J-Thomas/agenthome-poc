namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies the safe display role of a retained redacted Human Review preview.</summary>
public enum HumanReviewPreviewKind
{
    /// <summary>No supported preview kind was supplied.</summary>
    Unknown = 0,
    /// <summary>A redacted summary of the proposed action or continuation.</summary>
    Action = 1,
    /// <summary>A redacted summary of the expected result or consequence.</summary>
    Result = 2,
    /// <summary>A redacted summary of the evidence used to present the review.</summary>
    Evidence = 3
}
