namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Preserves bounded signature evidence without allowing artifact metadata to assert trust.</summary>
/// <param name="Algorithm">The canonical signature algorithm.</param>
/// <param name="KeyId">The server-configured verification-key identifier.</param>
/// <param name="Signature">The base64-encoded signature.</param>
public sealed record CapabilityArtifactSignatureEvidence(string Algorithm, string KeyId, string Signature);
