namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies the exact governed release boundary whose receipt a continuation completion retains.</summary>
public enum HumanReviewContinuationReleaseKind
{
    /// <summary>No supported release kind was supplied.</summary>
    Unknown = 0,

    /// <summary>The receipt proves release of the reviewed continuation without an effect-attempt receipt.</summary>
    Continuation = 1,

    /// <summary>The receipt proves release of a reviewed pre-dispatch effect attempt.</summary>
    PreDispatchEffect = 2
}
