namespace EmbodySense.Core.Application.Governance.Authority.Delegation.Models;

/// <summary>Classifies exact issuer-context resolution.</summary>
public enum AuthorityDelegationOriginResolutionStatus
{
    /// <summary>No supported posture was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact immutable issuer context remains current and authored.</summary>
    Current = 1,
    /// <summary>The exact issuer context or authored restriction changed.</summary>
    Drifted = 2,
    /// <summary>The exact issuer context completed.</summary>
    Completed = 3,
    /// <summary>The exact issuer context was not found.</summary>
    NotFound = 4,
    /// <summary>The supplied issuer context is malformed.</summary>
    Invalid = 5,
    /// <summary>Issuer truth is unavailable.</summary>
    Unavailable = 6,
    /// <summary>Issuer evidence cannot prove one posture.</summary>
    Ambiguous = 7,
}
