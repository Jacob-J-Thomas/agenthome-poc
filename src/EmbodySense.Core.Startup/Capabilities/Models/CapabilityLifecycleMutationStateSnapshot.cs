namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Projects the current safe capability lifecycle state after a mutation attempt.</summary>
/// <param name="CapabilityId">The canonical capability identity.</param>
/// <param name="Version">The exact current or last-retained version.</param>
/// <param name="IsEnabled">Whether lifecycle policy currently permits admission.</param>
/// <param name="IsRemoved">Whether the capability identity is tombstoned.</param>
/// <param name="Revision">The exact lifecycle revision.</param>
/// <param name="UpdatedAtUtc">The trusted mutation time.</param>
public sealed record CapabilityLifecycleMutationStateSnapshot(string CapabilityId, string Version, bool IsEnabled, bool IsRemoved, long Revision, DateTimeOffset UpdatedAtUtc);
