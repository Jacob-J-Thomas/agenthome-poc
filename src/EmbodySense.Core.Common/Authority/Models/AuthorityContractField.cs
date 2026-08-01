namespace EmbodySense.Core.Common.Authority.Models;

/// <summary>
/// Identifies a closed authority-contract field location without reflecting caller-controlled values.
/// </summary>
public enum AuthorityContractField
{
    /// <summary>The field is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The whole contract.</summary>
    Contract = 1,
    /// <summary>The schema version.</summary>
    SchemaVersion = 2,
    /// <summary>The profile identifier.</summary>
    ProfileId = 3,
    /// <summary>The profile revision.</summary>
    Revision = 4,
    /// <summary>The profile status.</summary>
    Status = 5,
    /// <summary>The authority purpose.</summary>
    Purpose = 6,
    /// <summary>The provenance object.</summary>
    Provenance = 7,
    /// <summary>The provenance actor identifier.</summary>
    ProvenanceActorId = 8,
    /// <summary>The provenance kind.</summary>
    ProvenanceKind = 9,
    /// <summary>The issued timestamp.</summary>
    IssuedAtUtc = 10,
    /// <summary>The expiry timestamp.</summary>
    ExpiresAtUtc = 11,
    /// <summary>The authority ceiling.</summary>
    Ceiling = 12,
    /// <summary>The exact capability ceiling collection.</summary>
    Capabilities = 13,
    /// <summary>The data-class ceiling collection.</summary>
    DataClasses = 14,
    /// <summary>The target-count ceiling.</summary>
    MaxTargetCount = 15,
    /// <summary>The side-effect ceiling.</summary>
    MaxSideEffectClass = 16,
    /// <summary>The boundary-condition collection.</summary>
    BoundaryConditions = 17,
    /// <summary>The boundary decision.</summary>
    BoundaryDecision = 18,
    /// <summary>The boundary reason.</summary>
    BoundaryReason = 19,
    /// <summary>The evaluation profile collection.</summary>
    Profiles = 20,
    /// <summary>The evaluation time.</summary>
    EvaluatedAtUtc = 21
}
