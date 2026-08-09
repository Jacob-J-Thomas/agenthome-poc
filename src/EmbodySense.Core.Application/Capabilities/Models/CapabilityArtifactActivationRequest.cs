namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Requests atomic activation of one immutable staged artifact.</summary>
/// <param name="Manifest">The validated manifest.</param>
/// <param name="ExpectedRevision">The expected activation-state revision.</param>
/// <param name="OperationId">The canonical idempotency key.</param>
public sealed record CapabilityArtifactActivationRequest(CapabilityArtifactManifest Manifest, long ExpectedRevision, string OperationId);
