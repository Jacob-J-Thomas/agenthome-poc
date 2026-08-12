namespace EmbodySense.Core.Common.Loops.Execution.Authority.Models;

/// <summary>Identifies one stable effect-authority contract validation failure.</summary>
public enum GovernedLoopEffectAuthorityValidationErrorCode
{
    /// <summary>A required contract or field is absent.</summary>
    Required = 1,

    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchemaVersion = 2,

    /// <summary>An identifier is not a bounded canonical lowercase token.</summary>
    InvalidIdentity = 3,

    /// <summary>A closed enumeration contains an unsupported value.</summary>
    InvalidEnumeration = 4,

    /// <summary>A hash is not canonical lowercase SHA-256 evidence.</summary>
    InvalidHash = 5,

    /// <summary>The stored content hash does not match the immutable decision.</summary>
    HashMismatch = 6,

    /// <summary>A timestamp or trusted-time relationship is invalid.</summary>
    InvalidTimestamp = 7,

    /// <summary>A finite collection or numeric bound was exceeded.</summary>
    LimitExceeded = 8,

    /// <summary>An admitted or current authority proof is malformed.</summary>
    InvalidProof = 9,

    /// <summary>Current proof does not retain the exact admitted grant or binding.</summary>
    BindingMismatch = 10,

    /// <summary>Current or effective authority widens an admitted dimension.</summary>
    AuthorityWidening = 11,

    /// <summary>Capability pins are duplicated, malformed, drifted, or outside a bound ceiling.</summary>
    CapabilityMismatch = 12,

    /// <summary>Disposition, reason, proof presence, or empty-authority posture is inconsistent.</summary>
    InvalidComposition = 13
}
