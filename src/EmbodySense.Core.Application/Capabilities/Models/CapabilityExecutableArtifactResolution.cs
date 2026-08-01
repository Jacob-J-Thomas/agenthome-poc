using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns a server-owned execution lease for one exact proved activation.</summary>
/// <param name="Status">The resolution outcome.</param>
/// <param name="Lease">The activation-bound execution lease when available.</param>
/// <param name="Detail">A safe diagnostic.</param>
public sealed record CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus Status, ICapabilityExecutableArtifactLease? Lease, string Detail);
