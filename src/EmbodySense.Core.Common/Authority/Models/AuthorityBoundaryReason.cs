namespace EmbodySense.Core.Common.Authority.Models;

/// <summary>
/// Identifies a closed reason for a direct, review, pause, or denial boundary decision.
/// </summary>
public enum AuthorityBoundaryReason
{
    /// <summary>The reason is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>No boundary condition was present.</summary>
    NoBoundary = 1,
    /// <summary>Mandatory review was declared.</summary>
    MandatoryReview = 2,
    /// <summary>Explicit human approval is required.</summary>
    HumanApprovalRequired = 3,
    /// <summary>The profile revision remains a draft.</summary>
    ProfileDraft = 4,
    /// <summary>The profile revision is suspended.</summary>
    ProfileSuspended = 5,
    /// <summary>The profile revision is retired.</summary>
    ProfileRetired = 6,
    /// <summary>The profile has expired.</summary>
    ProfileExpired = 7,
    /// <summary>The input contract is invalid or unsafe.</summary>
    InvalidContract = 8,
    /// <summary>The evidence is stale.</summary>
    StaleEvidence = 9,
    /// <summary>Concurrent or conflicting state requires escalation.</summary>
    ConflictingState = 10,
    /// <summary>User intent is uncertain.</summary>
    UncertainUserIntent = 11,
    /// <summary>The requested target count exceeds the ceiling.</summary>
    TargetLimitExceeded = 12,
    /// <summary>The requested data classification exceeds the ceiling.</summary>
    DataClassExceeded = 13,
    /// <summary>The requested side-effect class exceeds the ceiling.</summary>
    SideEffectExceeded = 14,
    /// <summary>External publication crosses a declared boundary.</summary>
    ExternalPublication = 15,
    /// <summary>Irreversible action crosses a declared boundary.</summary>
    IrreversibleAction = 16,
    /// <summary>Recurring work crosses a declared boundary.</summary>
    Recurrence = 17
}
