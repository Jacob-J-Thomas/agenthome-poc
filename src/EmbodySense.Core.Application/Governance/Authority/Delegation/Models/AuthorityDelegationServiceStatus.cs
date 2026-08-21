namespace EmbodySense.Core.Application.Governance.Authority.Delegation.Models;

/// <summary>Classifies one fail-closed delegated-authority creation or revalidation result.</summary>
public enum AuthorityDelegationServiceStatus
{
    /// <summary>No supported result was produced.</summary>
    Unknown = 0,
    /// <summary>A new hash-valid envelope was created.</summary>
    Created = 1,
    /// <summary>An existing envelope remains valid for the exact requested use.</summary>
    Valid = 2,
    /// <summary>The request or envelope contract is malformed or forged.</summary>
    InvalidContract = 3,
    /// <summary>The requested authority exceeds the exact parent, origin, or target maximum.</summary>
    OutsideParentAuthority = 4,
    /// <summary>The supplied parent issuer identity does not exactly match the envelope.</summary>
    OriginMismatch = 5,
    /// <summary>The exact origin is no longer current, authored, or complete.</summary>
    OriginDrifted = 6,
    /// <summary>The exact origin truth is unavailable.</summary>
    OriginUnavailable = 7,
    /// <summary>The supplied or current target does not exactly match the envelope.</summary>
    TargetMismatch = 8,
    /// <summary>The exact target truth is unavailable.</summary>
    TargetUnavailable = 9,
    /// <summary>The parent grant is not yet effective.</summary>
    ParentNotEffective = 10,
    /// <summary>The parent grant is suspended.</summary>
    ParentSuspended = 11,
    /// <summary>The parent grant is revoked.</summary>
    ParentRevoked = 12,
    /// <summary>The parent grant is expired.</summary>
    ParentExpired = 13,
    /// <summary>The exact parent grant or dependency binding has been replaced or drifted.</summary>
    ParentReplaced = 14,
    /// <summary>The exact parent execution has completed.</summary>
    ParentCompleted = 15,
    /// <summary>The envelope has not reached its inclusive effective instant.</summary>
    EnvelopeNotEffective = 16,
    /// <summary>The envelope reached its exclusive expiry instant.</summary>
    EnvelopeExpired = 17,
    /// <summary>The exact target-completion boundary has completed.</summary>
    EnvelopeCompleted = 18,
    /// <summary>One required source or trusted-time result is unavailable.</summary>
    Unavailable = 19,
    /// <summary>Available evidence does not prove one consistent posture.</summary>
    Ambiguous = 20,
    /// <summary>The exact envelope creation operation was already committed and its original envelope was replayed.</summary>
    Replayed = 21,
    /// <summary>The envelope identity was already reserved for a different exact creation request.</summary>
    EnvelopeIdConflict = 22,
}
