namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Explains one bounded dependent impact for a proposed capability lifecycle change.</summary>
/// <param name="DependentKind">The dependent domain.</param>
/// <param name="DependentIdentity">The stable dependent identity.</param>
/// <param name="DependentRevision">The exact dependent revision.</param>
/// <param name="RequirementKind">Whether the dependency is required or optional.</param>
/// <param name="CompatibleVersionRange">The declared compatible version range.</param>
/// <param name="IsCompatible">Whether the proposed target remains compatible.</param>
/// <param name="AuthorityPosture">The non-granting dependent authority posture.</param>
/// <param name="Outcome">The enforced proposed dependent outcome.</param>
public sealed record CapabilityLifecycleImpactSnapshot(string DependentKind, string DependentIdentity, string DependentRevision, string RequirementKind, string CompatibleVersionRange, bool IsCompatible, string AuthorityPosture, string Outcome);
