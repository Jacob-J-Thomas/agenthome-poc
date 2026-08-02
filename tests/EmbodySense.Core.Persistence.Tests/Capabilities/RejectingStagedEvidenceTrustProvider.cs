using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class RejectingStagedEvidenceTrustProvider : ICapabilityArtifactStateTrustProvider
{
    private readonly ICapabilityArtifactStateTrustProvider _inner;

    internal RejectingStagedEvidenceTrustProvider(ICapabilityArtifactStateTrustProvider inner) => _inner = inner;

    public Task<string> AuthenticateStagedEvidenceAsync(string workspaceIdentity, string artifactDigest, string evidenceDigest, CancellationToken cancellationToken = default) => _inner.AuthenticateStagedEvidenceAsync(workspaceIdentity, artifactDigest, evidenceDigest, cancellationToken);

    public Task<bool> VerifyStagedEvidenceAsync(string workspaceIdentity, string artifactDigest, string evidenceDigest, string authenticationTag, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<CapabilityArtifactTrustState?> ReadActivationAsync(string workspaceIdentity, CancellationToken cancellationToken = default) => _inner.ReadActivationAsync(workspaceIdentity, cancellationToken);

    public Task<CapabilityArtifactTrustState> InitializeActivationAsync(string workspaceIdentity, string contentDigest, CancellationToken cancellationToken = default) => _inner.InitializeActivationAsync(workspaceIdentity, contentDigest, cancellationToken);

    public Task<string> AuthenticateActivationAsync(string workspaceIdentity, long revision, string contentDigest, CancellationToken cancellationToken = default) => _inner.AuthenticateActivationAsync(workspaceIdentity, revision, contentDigest, cancellationToken);

    public Task<bool> VerifyActivationAsync(string workspaceIdentity, long revision, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default) => _inner.VerifyActivationAsync(workspaceIdentity, revision, contentDigest, authenticationTag, cancellationToken);

    public Task<CapabilityArtifactTrustState> AdvanceActivationAsync(string workspaceIdentity, long expectedRevision, string expectedContentDigest, long newRevision, string newContentDigest, CancellationToken cancellationToken = default) => _inner.AdvanceActivationAsync(workspaceIdentity, expectedRevision, expectedContentDigest, newRevision, newContentDigest, cancellationToken);
}
