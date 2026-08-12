using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Returns exact loop publication ownership and capability binding evidence.</summary>
/// <param name="Status">The closed exact-dependency posture.</param>
/// <param name="PublicationPin">The exact resolved publication pin when valid.</param>
/// <param name="Artifact">The exact immutable graph artifact bound to the publication.</param>
/// <param name="OwningRole">The exact contextual-role revision that owns the loop.</param>
/// <param name="CapabilityIds">The bounded canonical capabilities assigned to the loop.</param>
/// <param name="EvidenceHash">The canonical current binding-evidence digest.</param>
public sealed record GovernedLoopGrantBindingResolution(
    AuthorityGrantDependencyStatus Status,
    GovernedLoopRevisionPublicationPin? PublicationPin,
    GovernedLoopGraphRevisionArtifact? Artifact,
    ContextualRoleRevisionPin? OwningRole,
    IReadOnlyList<string> CapabilityIds,
    string EvidenceHash)
{
    /// <summary>Gets a defensive immutable copy of loop capability identifiers.</summary>
    public IReadOnlyList<string> CapabilityIds { get; } = CapabilityIds is null ? null! : Array.AsReadOnly(CapabilityIds.ToArray());
}
