namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Projects one safe dependent requirement without granting its authority.</summary>
/// <param name="Kind">The dependent domain.</param>
/// <param name="Identity">The bounded dependent identity.</param>
/// <param name="Revision">The exact dependent revision.</param>
/// <param name="RequirementKind">Whether the requirement is required or optional.</param>
/// <param name="CompatibleVersionRange">The declared compatible version range.</param>
/// <param name="AuthorityPosture">The non-granting authority relationship.</param>
public sealed record CapabilityPostureDependentSnapshot(string Kind, string Identity, string Revision, string RequirementKind, string CompatibleVersionRange, string AuthorityPosture);
