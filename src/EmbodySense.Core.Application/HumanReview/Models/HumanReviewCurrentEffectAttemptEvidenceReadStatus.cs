namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Identifies the closed result of rereading current server-derived effect-attempt evidence.</summary>
public enum HumanReviewCurrentEffectAttemptEvidenceReadStatus
{
    /// <summary>No supported result was supplied.</summary>
    Unknown = 0,

    /// <summary>Detached current identity and preparation evidence was returned.</summary>
    Current = 1,

    /// <summary>No canonical attempt exists for the exact reviewed effect reference.</summary>
    Missing = 2,

    /// <summary>Retained canonical attempt evidence was malformed, unsupported, or internally inconsistent.</summary>
    Corrupt = 3,

    /// <summary>Current attempt evidence no longer matches the reviewed effect reference.</summary>
    Stale = 4,

    /// <summary>The bounded canonical read could not complete.</summary>
    Unavailable = 5,
}
