namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>Declares one exact canonical capability identity and compatible-version range.</summary>
public sealed record CapabilityDependency(CapabilityId CapabilityId, CapabilityVersionRange CompatibleVersionRange);
