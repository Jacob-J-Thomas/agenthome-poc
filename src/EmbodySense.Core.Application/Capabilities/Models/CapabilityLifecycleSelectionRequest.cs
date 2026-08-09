using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Captures the complete browser-safe lifecycle preview selection.</summary>
/// <param name="OperationId">The idempotent operation identity.</param>
/// <param name="Kind">The selected lifecycle transition.</param>
/// <param name="CapabilityId">The selected capability identity.</param>
/// <param name="TargetVersion">An optional exact target version for enable or upgrade.</param>
public sealed record CapabilityLifecycleSelectionRequest(string OperationId, CapabilityLifecycleOperationKind Kind, CapabilityId CapabilityId, CapabilityVersion? TargetVersion = null);
