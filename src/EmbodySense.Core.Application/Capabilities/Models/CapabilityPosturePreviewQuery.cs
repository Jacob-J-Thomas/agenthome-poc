using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Requests a read-only lifecycle impact projection that cannot authorize later mutation.</summary>
/// <param name="CapabilityId">The exact capability identity.</param>
/// <param name="Operation">The lifecycle transition being inspected.</param>
/// <param name="TargetVersion">The replacement version required for upgrade and ignored for other operations.</param>
public sealed record CapabilityPosturePreviewQuery(CapabilityId CapabilityId, CapabilityLifecycleOperationKind Operation, CapabilityVersion? TargetVersion = null);
