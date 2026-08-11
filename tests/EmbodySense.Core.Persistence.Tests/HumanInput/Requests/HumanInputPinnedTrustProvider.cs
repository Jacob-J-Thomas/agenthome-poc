using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Requests;

internal sealed class HumanInputPinnedTrustProvider(
    string expectedWorkspaceIdentity,
    long generation,
    string contentDigest,
    string authenticationTag) : ICapabilityCatalogTrustProvider
{
    public int MaximumAuthenticationTagUtf8Bytes => 128;

    public void RequireDisjointWorkspace(string workspaceRootPath)
    {
    }

    public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CapabilityCatalogTrustState? state = string.Equals(workspaceIdentity, expectedWorkspaceIdentity, StringComparison.Ordinal)
            ? new CapabilityCatalogTrustState(workspaceIdentity, generation, contentDigest, null, null)
            : null;
        return Task.FromResult(state);
    }

    public Task<CapabilityCatalogTrustState> InitializeAsync(
        string workspaceIdentity,
        long initialGeneration,
        string initialContentDigest,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<string> AuthenticateArtifactAsync(
        string workspaceIdentity,
        long candidateGeneration,
        string candidateContentDigest,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> VerifyArtifactAsync(
        string workspaceIdentity,
        long candidateGeneration,
        string candidateContentDigest,
        string candidateAuthenticationTag,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            string.Equals(workspaceIdentity, expectedWorkspaceIdentity, StringComparison.Ordinal)
            && candidateGeneration == generation
            && string.Equals(candidateContentDigest, contentDigest, StringComparison.Ordinal)
            && string.Equals(candidateAuthenticationTag, authenticationTag, StringComparison.Ordinal));
    }

    public Task<CapabilityCatalogTrustState> AdvanceAsync(
        string workspaceIdentity,
        long expectedGeneration,
        string expectedContentDigest,
        long newGeneration,
        string newContentDigest,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
