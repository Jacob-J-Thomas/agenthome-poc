using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Snapshots one grant's complete bounded immutable lineage and operations.</summary>
/// <param name="CurrentGrant">The exact current immutable revision.</param>
/// <param name="Revisions">All retained immutable revisions in increasing order.</param>
/// <param name="Operations">All retained operations for this grant in durable order.</param>
public sealed record AuthorityGrantStoreSnapshot(
    AuthorityGrant CurrentGrant,
    IReadOnlyList<AuthorityGrant> Revisions,
    IReadOnlyList<AuthorityGrantOperationEvidence> Operations)
{
    /// <summary>Gets a defensive immutable copy of retained revisions.</summary>
    public IReadOnlyList<AuthorityGrant> Revisions { get; } = Revisions is null ? null! : Array.AsReadOnly(Revisions.ToArray());

    /// <summary>Gets a defensive immutable copy of retained operations.</summary>
    public IReadOnlyList<AuthorityGrantOperationEvidence> Operations { get; } = Operations is null ? null! : Array.AsReadOnly(Operations.ToArray());
}
