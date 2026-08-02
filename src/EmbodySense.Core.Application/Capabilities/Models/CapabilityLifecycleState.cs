using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Projects the current authenticated capability lifecycle state without assigning authority.</summary>
/// <param name="Descriptor">The current or last tombstoned descriptor.</param>
/// <param name="ArtifactDigest">The current or last tombstoned immutable artifact.</param>
/// <param name="IsEnabled">Whether lifecycle policy permits admission.</param>
/// <param name="IsRemoved">Whether the identity is tombstoned.</param>
/// <param name="Revision">The last lifecycle revision changing this capability.</param>
/// <param name="LastOperationId">The last operation changing this capability.</param>
/// <param name="UpdatedAtUtc">The trusted mutation time.</param>
public sealed record CapabilityLifecycleState(CapabilityDescriptor Descriptor, CapabilityIntegrityDigest ArtifactDigest, bool IsEnabled, bool IsRemoved, long Revision, string LastOperationId, DateTimeOffset UpdatedAtUtc);
