using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Projects one immutable prior capability lifecycle state.</summary>
/// <param name="Descriptor">The prior descriptor.</param>
/// <param name="ArtifactDigest">The prior immutable artifact.</param>
/// <param name="WasEnabled">Whether the prior state permitted admission.</param>
/// <param name="WasRemoved">Whether the prior state was tombstoned.</param>
/// <param name="Revision">The prior lifecycle revision.</param>
/// <param name="OperationId">The operation that established the prior state.</param>
/// <param name="ChangedAtUtc">The trusted prior mutation time.</param>
public sealed record CapabilityLifecycleHistoryEntry(CapabilityDescriptor Descriptor, CapabilityIntegrityDigest ArtifactDigest, bool WasEnabled, bool WasRemoved, long Revision, string OperationId, DateTimeOffset ChangedAtUtc);
