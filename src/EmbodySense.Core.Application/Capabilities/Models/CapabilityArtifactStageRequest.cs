using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Requests immutable staging of already verified artifact bytes.</summary>
/// <param name="Manifest">The validated manifest.</param>
/// <param name="Content">The verified content.</param>
/// <param name="Trust">The server-owned verified trust evidence.</param>
public sealed record CapabilityArtifactStageRequest(CapabilityArtifactManifest Manifest, CapabilityArtifactContent Content, CapabilityArtifactTrustDecision Trust);
