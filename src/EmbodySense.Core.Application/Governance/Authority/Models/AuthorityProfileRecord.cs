using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Models;

/// <summary>Projects one profile's immutable lineage, current declaration, operation evidence, and optional tombstone.</summary>
/// <param name="ProfileId">The stable canonical identifier.</param>
/// <param name="CurrentProfile">The latest immutable declaration.</param>
/// <param name="CurrentHash">The canonical hash of the latest declaration.</param>
/// <param name="Revisions">All immutable profile revisions in increasing order.</param>
/// <param name="Tombstone">The irreversible tombstone when present.</param>
/// <param name="Operations">The bounded immutable lifecycle evidence for this profile.</param>
public sealed record AuthorityProfileRecord(AuthorityProfileId ProfileId, AuthorityProfile CurrentProfile, AuthorityProfileHash CurrentHash, IReadOnlyList<AuthorityProfileRevisionEvidence> Revisions, AuthorityProfileTombstone? Tombstone, IReadOnlyList<AuthorityProfileOperationReceipt> Operations)
{
    /// <summary>Gets a defensive immutable copy of retained revision evidence.</summary>
    public IReadOnlyList<AuthorityProfileRevisionEvidence> Revisions { get; } = Revisions is null ? null! : Array.AsReadOnly(Revisions.ToArray());
    /// <summary>Gets a defensive immutable copy of retained operation evidence.</summary>
    public IReadOnlyList<AuthorityProfileOperationReceipt> Operations { get; } = Operations is null ? null! : Array.AsReadOnly(Operations.ToArray());
}
