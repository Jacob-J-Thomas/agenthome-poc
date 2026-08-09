namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Projects one safe dependent requirement without transferring its domain authority.</summary>
/// <param name="Kind">The dependent domain.</param>
/// <param name="Identity">The bounded dependent identity.</param>
/// <param name="Revision">The exact dependent revision.</param>
/// <param name="RequirementKind">Whether the requirement is required or optional.</param>
/// <param name="CompatibleVersionRange">The declared compatible capability range.</param>
/// <param name="AuthorityPosture">The non-granting authority relationship.</param>
public sealed record CapabilityPostureDependentProjection(
    CapabilityDependentKind Kind,
    string Identity,
    string Revision,
    CapabilityRequirementKind RequirementKind,
    string CompatibleVersionRange,
    CapabilityAuthorityPosture AuthorityPosture);
