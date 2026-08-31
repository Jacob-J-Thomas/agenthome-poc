namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Describes whether current canonical persistence proves one exact pre-dispatch effect release.</summary>
public enum HumanReviewPreDispatchEffectReleaseEvidenceReadStatus
{
    /// <summary>The source returned no supported posture.</summary>
    Unknown = 0,

    /// <summary>The exact canonical run and effect attempt retain the complete matching release chain.</summary>
    Current = 1,

    /// <summary>No terminal release evidence exists for the exact retained effect.</summary>
    Missing = 2,

    /// <summary>Canonical evidence exists but differs from the supplied expectation or current effect coordinates.</summary>
    Stale = 3,

    /// <summary>Canonical evidence is malformed, incomplete, or internally inconsistent.</summary>
    Corrupt = 4,

    /// <summary>Current canonical evidence could not be read conclusively.</summary>
    Unavailable = 5,
}
