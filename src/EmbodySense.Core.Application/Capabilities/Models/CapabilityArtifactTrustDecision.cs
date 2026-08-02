namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns bounded server-owned trust evidence.</summary>
/// <param name="Status">The verification outcome.</param>
/// <param name="Verifier">The non-secret verifier identity.</param>
/// <param name="Detail">A bounded safe explanation.</param>
public sealed record CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus Status, string Verifier, string Detail);
