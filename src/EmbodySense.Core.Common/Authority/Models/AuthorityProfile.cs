namespace EmbodySense.Core.Common.Authority.Models;

/// <summary>
/// Defines one schema-version-1 user-owned authority profile revision without self-granting trust, approval, assignment, or execution rights.
/// </summary>
/// <param name="SchemaVersion">The authority-profile schema version.</param>
/// <param name="ProfileId">The stable profile identifier.</param>
/// <param name="Revision">The positive profile revision.</param>
/// <param name="Status">The declared lifecycle posture.</param>
/// <param name="Purpose">The bounded human-readable purpose.</param>
/// <param name="Provenance">The non-authoritative provenance record.</param>
/// <param name="IssuedAtUtc">The exact UTC profile issue time.</param>
/// <param name="ExpiresAtUtc">The optional exact UTC expiry boundary, inclusive for expiry checks.</param>
/// <param name="Ceiling">The bounded candidate ceiling.</param>
/// <param name="BoundaryConditions">The closed boundary conditions to evaluate with the profile.</param>
public sealed record AuthorityProfile(
    int SchemaVersion,
    AuthorityProfileId ProfileId,
    AuthorityProfileRevision Revision,
    AuthorityProfileStatus Status,
    AuthorityPurpose Purpose,
    AuthorityProvenance Provenance,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    AuthorityCeiling Ceiling,
    IReadOnlyList<AuthorityBoundaryCondition> BoundaryConditions)
{
    /// <summary>Gets the only supported experimental authority-profile schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets a defensive read-only snapshot of boundary conditions.</summary>
    public IReadOnlyList<AuthorityBoundaryCondition> BoundaryConditions { get; } = BoundaryConditions is null ? null! : Array.AsReadOnly(BoundaryConditions.ToArray());
}
