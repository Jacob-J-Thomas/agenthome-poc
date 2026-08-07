using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Describes one visible resolver decision for later admission or package evidence.</summary>
public sealed record CapabilityDependencyResolutionEvidence(CapabilityId SubjectId, CapabilityId DependencyId, CapabilityVersionRange CompatibleVersionRange, bool IsOptional, CapabilityDependencyResolutionOutcome Outcome, CapabilityResolvedPin? Pin, string Detail);
