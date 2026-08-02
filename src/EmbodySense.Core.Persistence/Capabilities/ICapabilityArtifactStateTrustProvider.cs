using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Owns artifact-evidence authentication and monotonic activation state outside mutable workspace storage.</summary>
public interface ICapabilityArtifactStateTrustProvider
{
    /// <summary>Authenticates one immutable staged-evidence digest under its exact artifact digest.</summary>
    Task<string> AuthenticateStagedEvidenceAsync(string workspaceIdentity, string artifactDigest, string evidenceDigest, CancellationToken cancellationToken = default);

    /// <summary>Verifies one immutable staged-evidence authentication tag.</summary>
    Task<bool> VerifyStagedEvidenceAsync(string workspaceIdentity, string artifactDigest, string evidenceDigest, string authenticationTag, CancellationToken cancellationToken = default);

    /// <summary>Reads the server-owned activation anchor.</summary>
    Task<CapabilityArtifactTrustState?> ReadActivationAsync(string workspaceIdentity, CancellationToken cancellationToken = default);

    /// <summary>Initializes the activation anchor at revision zero.</summary>
    Task<CapabilityArtifactTrustState> InitializeActivationAsync(string workspaceIdentity, string contentDigest, CancellationToken cancellationToken = default);

    /// <summary>Authenticates current or direct-successor activation state.</summary>
    Task<string> AuthenticateActivationAsync(string workspaceIdentity, long revision, string contentDigest, CancellationToken cancellationToken = default);

    /// <summary>Verifies one activation document without treating it as current.</summary>
    Task<bool> VerifyActivationAsync(string workspaceIdentity, long revision, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default);

    /// <summary>Advances the activation anchor by exactly one revision.</summary>
    Task<CapabilityArtifactTrustState> AdvanceActivationAsync(string workspaceIdentity, long expectedRevision, string expectedContentDigest, long newRevision, string newContentDigest, CancellationToken cancellationToken = default);
}
