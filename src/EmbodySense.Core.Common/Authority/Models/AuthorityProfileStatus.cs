namespace EmbodySense.Core.Common.Authority.Models;

/// <summary>
/// Identifies the lifecycle posture declared by an authority-profile revision.
/// </summary>
public enum AuthorityProfileStatus
{
    /// <summary>The status is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The profile is inspectable but must pause and escalate before use.</summary>
    Draft = 1,
    /// <summary>The profile may be evaluated by an externally governed authority source.</summary>
    Active = 2,
    /// <summary>The profile must pause and escalate before use.</summary>
    Suspended = 3,
    /// <summary>The profile is retired and denies use.</summary>
    Retired = 4
}
