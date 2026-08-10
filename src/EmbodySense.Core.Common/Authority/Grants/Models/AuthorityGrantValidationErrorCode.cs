namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Identifies a closed value-free authority-grant contract failure.</summary>
public enum AuthorityGrantValidationErrorCode
{
    /// <summary>The error is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>A required contract value is absent.</summary>
    Required = 1,
    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchemaVersion = 2,
    /// <summary>An identity or exact pin is malformed.</summary>
    InvalidIdentity = 3,
    /// <summary>An immutable predecessor or successor relationship is invalid.</summary>
    InvalidLineage = 4,
    /// <summary>A lifecycle operation or status is unsupported.</summary>
    InvalidLifecycle = 5,
    /// <summary>A timestamp or boundary is non-UTC, default, or inconsistent.</summary>
    InvalidBoundary = 6,
    /// <summary>The requested ceiling is malformed.</summary>
    InvalidCeiling = 7,
    /// <summary>A canonical hash is malformed or does not match content.</summary>
    InvalidHash = 8,
    /// <summary>The immutable successor would widen a narrowed dimension.</summary>
    AuthorityWidening = 9,
    /// <summary>The serialized JSON is malformed, unsafe, oversized, or outside the closed schema.</summary>
    InvalidJson = 10,
    /// <summary>The serialized JSON is valid but not the single canonical schema-version-1 representation.</summary>
    NonCanonicalJson = 11,
}
