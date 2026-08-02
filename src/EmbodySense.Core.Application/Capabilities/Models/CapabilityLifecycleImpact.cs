namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Explains how one exact dependent requirement is affected by a proposed capability transition.</summary>
/// <param name="DependentKind">The dependent domain.</param>
/// <param name="DependentIdentity">The stable dependent identity.</param>
/// <param name="DependentRevision">The exact dependent revision.</param>
/// <param name="RequirementKind">Whether the dependency is required or optional.</param>
/// <param name="CompatibleVersionRange">The declared compatible range.</param>
/// <param name="IsCompatible">Whether the target capability version satisfies the range.</param>
/// <param name="AuthorityPosture">The non-granting authority posture.</param>
/// <param name="Outcome">The enforced proposed outcome.</param>
public sealed record CapabilityLifecycleImpact(CapabilityDependentKind DependentKind, string DependentIdentity, string DependentRevision, CapabilityRequirementKind RequirementKind, string CompatibleVersionRange, bool IsCompatible, CapabilityAuthorityPosture AuthorityPosture, CapabilityLifecycleImpactOutcome Outcome);
