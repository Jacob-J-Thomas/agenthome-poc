namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Requests one idempotent, revision-checked artifact intake.</summary>
/// <param name="Manifest">The artifact manifest.</param>
/// <param name="ExpectedActivationRevision">The expected durable activation-state revision.</param>
/// <param name="OperationId">The canonical idempotency key.</param>
public sealed record CapabilityArtifactIntakeRequest(CapabilityArtifactManifest Manifest, long ExpectedActivationRevision, string OperationId);
