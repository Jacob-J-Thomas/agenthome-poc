namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns deterministic exact pins and complete bounded evidence without assigning capabilities to loops.</summary>
public sealed record CapabilityDependencyResolutionResult(bool IsResolved, IReadOnlyList<CapabilityResolvedPin> Selected, IReadOnlyList<CapabilityDependencyResolutionEvidence> Evidence);
