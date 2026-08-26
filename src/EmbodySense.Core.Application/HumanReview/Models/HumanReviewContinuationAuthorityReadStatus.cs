namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Identifies the closed current-authority revalidation posture for one exact continuation binding.</summary>
public enum HumanReviewContinuationAuthorityReadStatus
{
    /// <summary>No supported revalidation posture was supplied.</summary>
    Unknown = 0,

    /// <summary>Current authority, role, grant, capability, profile, implementation, actuator, target, precondition, and payload evidence remain exact.</summary>
    Current = 1,

    /// <summary>Current authority is narrower than the reviewed continuation requires.</summary>
    Narrowed = 2,

    /// <summary>Current authority has been revoked, expired, cancelled, or otherwise cannot release the continuation.</summary>
    Revoked = 3,

    /// <summary>Current evidence drifted from the exact reviewed binding.</summary>
    Stale = 4,

    /// <summary>Current authority could not be determined from trusted canonical dependencies.</summary>
    Unavailable = 5,

    /// <summary>The query or returned source evidence was invalid.</summary>
    Invalid = 6,
}
