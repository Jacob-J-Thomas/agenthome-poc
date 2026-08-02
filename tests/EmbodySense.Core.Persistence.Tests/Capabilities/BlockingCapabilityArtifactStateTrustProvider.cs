using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class BlockingCapabilityArtifactStateTrustProvider(ICapabilityArtifactStateTrustProvider inner) : ICapabilityArtifactStateTrustProvider
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool BlockNextActivationRead { get; set; }

    internal Task Entered => _entered.Task;

    internal void Release() => _release.TrySetResult();

    public Task<string> AuthenticateStagedEvidenceAsync(string workspaceIdentity, string artifactDigest, string evidenceDigest, CancellationToken cancellationToken = default) => inner.AuthenticateStagedEvidenceAsync(workspaceIdentity, artifactDigest, evidenceDigest, cancellationToken);

    public Task<bool> VerifyStagedEvidenceAsync(string workspaceIdentity, string artifactDigest, string evidenceDigest, string authenticationTag, CancellationToken cancellationToken = default) => inner.VerifyStagedEvidenceAsync(workspaceIdentity, artifactDigest, evidenceDigest, authenticationTag, cancellationToken);

    public async Task<CapabilityArtifactTrustState?> ReadActivationAsync(string workspaceIdentity, CancellationToken cancellationToken = default)
    {
        if (BlockNextActivationRead)
        {
            BlockNextActivationRead = false;
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        return await inner.ReadActivationAsync(workspaceIdentity, cancellationToken);
    }

    public Task<CapabilityArtifactTrustState> InitializeActivationAsync(string workspaceIdentity, string contentDigest, CancellationToken cancellationToken = default) => inner.InitializeActivationAsync(workspaceIdentity, contentDigest, cancellationToken);

    public Task<string> AuthenticateActivationAsync(string workspaceIdentity, long revision, string contentDigest, CancellationToken cancellationToken = default) => inner.AuthenticateActivationAsync(workspaceIdentity, revision, contentDigest, cancellationToken);

    public Task<bool> VerifyActivationAsync(string workspaceIdentity, long revision, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default) => inner.VerifyActivationAsync(workspaceIdentity, revision, contentDigest, authenticationTag, cancellationToken);

    public Task<CapabilityArtifactTrustState> AdvanceActivationAsync(string workspaceIdentity, long expectedRevision, string expectedContentDigest, long newRevision, string newContentDigest, CancellationToken cancellationToken = default) => inner.AdvanceActivationAsync(workspaceIdentity, expectedRevision, expectedContentDigest, newRevision, newContentDigest, cancellationToken);
}
