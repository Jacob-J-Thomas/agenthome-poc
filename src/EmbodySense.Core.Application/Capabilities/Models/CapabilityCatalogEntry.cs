using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Projects one capability descriptor and its orthogonal server-owned lifecycle state.</summary>
/// <param name="Descriptor">The validated public descriptor.</param>
/// <param name="Lifecycle">The server-owned lifecycle axes.</param>
/// <param name="Revision">The positive entry revision.</param>
/// <param name="UpdatedAtUtc">The trusted store time of the last state transition.</param>
/// <param name="LastOperationId">The operation that last changed lifecycle state.</param>
public sealed record CapabilityCatalogEntry(CapabilityDescriptor Descriptor, CapabilityLifecycleSnapshot Lifecycle, long Revision, DateTimeOffset UpdatedAtUtc, string LastOperationId);
