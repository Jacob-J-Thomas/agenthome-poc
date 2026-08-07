namespace EmbodySense.Core.Common.Authority.Models;

/// <summary>
/// Identifies the non-authoritative evidence category for a profile revision.
/// </summary>
public enum AuthorityProvenanceKind
{
    /// <summary>The provenance category is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The profile was supplied through a user-owned declaration surface.</summary>
    UserDeclaration = 1,
    /// <summary>The profile was imported from a verified, user-selected artifact.</summary>
    ImportedArtifact = 2,
    /// <summary>The profile was reproduced from an audit record.</summary>
    AuditReplay = 3
}
