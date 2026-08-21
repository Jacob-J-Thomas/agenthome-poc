namespace EmbodySense.Core.Common.Authority.Delegation.Models;

/// <summary>Classifies bounded delegated-authority contract validation failures.</summary>
public enum AuthorityDelegationContractValidationErrorCode
{
    /// <summary>A required value is absent.</summary>
    Required = 1,
    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchema = 2,
    /// <summary>An identity or exact immutable pin is malformed.</summary>
    InvalidIdentity = 3,
    /// <summary>A closed enumeration value is unsupported.</summary>
    InvalidEnumeration = 4,
    /// <summary>A canonical hash is absent, malformed, or does not match content.</summary>
    InvalidHash = 5,
    /// <summary>A finite scalar or collection bound is exceeded.</summary>
    BoundExceeded = 6,
    /// <summary>A collection is duplicated, unordered, inconsistently counted, or hostile.</summary>
    InvalidCollection = 7,
    /// <summary>The role, loop, or node target matrix is inconsistent.</summary>
    InvalidTargetBinding = 8,
    /// <summary>The delegated authority exceeds a supplied maximum.</summary>
    AuthorityWidening = 9,
    /// <summary>Delegated capability pins do not exactly describe the delegated ceiling.</summary>
    CapabilityPinMismatch = 10,
    /// <summary>The local time or completion boundary is malformed or wider than its parent.</summary>
    InvalidBoundary = 11,
    /// <summary>The immutable parent, revocation, proof, or target evidence links do not agree.</summary>
    ParentLinkMismatch = 12,
    /// <summary>The complete envelope composition is internally inconsistent.</summary>
    InvalidComposition = 13,
}
