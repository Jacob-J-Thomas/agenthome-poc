namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Classifies the conclusive result retained for one exact continuation release operation.</summary>
public enum HumanReviewContinuationReleaseDisposition
{
    /// <summary>No supported release disposition was supplied.</summary>
    Unknown = 0,

    /// <summary>The exact release operation crossed its governed release boundary conclusively.</summary>
    Released = 1,

    /// <summary>The exact release operation conclusively did not cross its governed release boundary.</summary>
    NotReleased = 2,

    /// <summary>The release operation cannot be classified conclusively and must not support completion.</summary>
    Ambiguous = 3
}
