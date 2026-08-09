using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Requests a server-owned target for an artifact-bearing lifecycle transition.</summary>
/// <param name="Kind">The artifact-bearing transition, limited to enable or upgrade.</param>
/// <param name="CapabilityId">The selected capability identity.</param>
/// <param name="TargetVersion">The optional exact version filter.</param>
public sealed record CapabilityLifecycleTargetResolutionRequest(CapabilityLifecycleOperationKind Kind, CapabilityId CapabilityId, CapabilityVersion? TargetVersion = null);
