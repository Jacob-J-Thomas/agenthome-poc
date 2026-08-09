using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns one bounded server-owned lifecycle target resolution.</summary>
/// <param name="Status">The resolution outcome.</param>
/// <param name="Descriptor">The exact canonical descriptor only when available.</param>
/// <param name="ArtifactDigest">The exact immutable digest only when available.</param>
/// <param name="Detail">A bounded operator-facing explanation.</param>
public sealed record CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus Status, CapabilityDescriptor? Descriptor, CapabilityIntegrityDigest? ArtifactDigest, string Detail);
