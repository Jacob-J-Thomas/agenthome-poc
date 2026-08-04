namespace EmbodySense.Core.Common.Authority.Models;

/// <summary>
/// Identifies the closed, value-free authority-contract validation error vocabulary.
/// </summary>
public enum AuthorityContractErrorCode
{
    /// <summary>The error code is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>A required contract value is absent.</summary>
    Required = 1,
    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchemaVersion = 2,
    /// <summary>The profile identity is invalid.</summary>
    InvalidProfileId = 3,
    /// <summary>The revision is invalid.</summary>
    InvalidRevision = 4,
    /// <summary>The actor identity is invalid.</summary>
    InvalidActorId = 5,
    /// <summary>The purpose text is unsafe, noncanonical, or oversized.</summary>
    InvalidPurpose = 6,
    /// <summary>The profile status is unsupported.</summary>
    UnsupportedStatus = 7,
    /// <summary>The provenance kind is unsupported.</summary>
    UnsupportedProvenanceKind = 8,
    /// <summary>The profile timestamps are not exact UTC or are inconsistent.</summary>
    InvalidTimestamp = 9,
    /// <summary>The profile has expired at the requested evaluation time.</summary>
    Expired = 10,
    /// <summary>A ceiling is absent.</summary>
    CeilingRequired = 11,
    /// <summary>A ceiling collection exceeds its bound.</summary>
    CollectionOutOfRange = 12,
    /// <summary>A ceiling collection contains a missing item.</summary>
    CollectionItemRequired = 13,
    /// <summary>A ceiling collection contains a duplicate item.</summary>
    DuplicateCollectionItem = 14,
    /// <summary>An exact capability reference is incomplete.</summary>
    CapabilityIdentityRequired = 15,
    /// <summary>The maximum target count is outside the bounded range.</summary>
    TargetCountOutOfRange = 16,
    /// <summary>The maximum side-effect class is unsupported.</summary>
    UnsupportedSideEffectClass = 17,
    /// <summary>A boundary decision is unsupported.</summary>
    UnsupportedBoundaryDecision = 18,
    /// <summary>A boundary reason is unsupported.</summary>
    UnsupportedBoundaryReason = 19,
    /// <summary>A decision and reason are an unsafe or ambiguous pair.</summary>
    InvalidBoundaryCondition = 20,
    /// <summary>The intersection profile collection is missing or outside its bound.</summary>
    InvalidIntersectionProfiles = 21,
    /// <summary>The evaluation time is not exact UTC.</summary>
    InvalidEvaluationTime = 22,
    /// <summary>The serialized JSON is malformed, unsafe, or oversized.</summary>
    InvalidJson = 23,
    /// <summary>The serialized JSON is valid but not in the single canonical profile form.</summary>
    NonCanonicalJson = 24,
    /// <summary>An expected JSON object is absent or has the wrong token type.</summary>
    ObjectRequired = 25,
    /// <summary>A required JSON property is absent.</summary>
    PropertyRequired = 26,
    /// <summary>A JSON object includes a property outside the closed schema.</summary>
    UnknownProperty = 27,
    /// <summary>A JSON object repeats a property.</summary>
    DuplicateProperty = 28,
    /// <summary>A JSON property must be a string.</summary>
    StringRequired = 29,
    /// <summary>A JSON property must be a canonical integer.</summary>
    IntegerRequired = 30,
    /// <summary>A JSON property must be a Boolean value.</summary>
    BooleanRequired = 31,
    /// <summary>A JSON property must be an array.</summary>
    ArrayRequired = 32,
    /// <summary>The same profile identity and revision was supplied more than once.</summary>
    DuplicateProfileRevision = 33
}
