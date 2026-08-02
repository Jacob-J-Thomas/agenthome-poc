using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Requests one deterministic impact preview bound to an idempotent operation identity.</summary>
/// <param name="OperationId">The preview and mutation operation identity.</param>
/// <param name="Kind">The proposed lifecycle transition.</param>
/// <param name="CapabilityId">The target capability.</param>
/// <param name="TargetDescriptor">The server-owned descriptor required for enable and upgrade.</param>
/// <param name="TargetArtifactDigest">The server-owned immutable artifact required for enable and upgrade.</param>
/// <param name="Selection">The optional browser-safe server-validated selection identity that produced the target evidence.</param>
public sealed record CapabilityLifecyclePreviewRequest(string OperationId, CapabilityLifecycleOperationKind Kind, CapabilityId CapabilityId, CapabilityDescriptor? TargetDescriptor = null, CapabilityIntegrityDigest? TargetArtifactDigest = null, CapabilityLifecycleSelectionRequest? Selection = null);
