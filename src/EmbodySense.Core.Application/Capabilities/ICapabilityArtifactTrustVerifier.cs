using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Applies server-owned artifact trust policy; manifest metadata cannot implement this port.</summary>
public interface ICapabilityArtifactTrustVerifier
{
    /// <summary>Verifies exact digest and optional signature evidence under server-owned policy.</summary>
    Task<CapabilityArtifactTrustDecision> VerifyAsync(CapabilityArtifactManifest manifest, CapabilityIntegrityDigest actualDigest, CancellationToken cancellationToken = default);
}
