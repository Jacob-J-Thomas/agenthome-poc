using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Inference.Profiles;

internal sealed class MutableModelPersistenceTrustProvider : ICapabilityCatalogTrustProvider
{
    internal const string AuthenticationTag = "authenticated-model-persistence-test";
    private CapabilityCatalogTrustState? _state;

    public int MaximumAuthenticationTagUtf8Bytes => 64;

    public void RequireDisjointWorkspace(string workspaceRootPath)
    {
    }

    public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default)
        => Task.FromResult(_state);

    public Task<CapabilityCatalogTrustState> InitializeAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
    {
        _state = new CapabilityCatalogTrustState(workspaceIdentity, generation, contentDigest, null, null);
        return Task.FromResult(_state);
    }

    public Task<string> AuthenticateArtifactAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
        => Task.FromResult(AuthenticationTag);

    public Task<bool> VerifyArtifactAsync(string workspaceIdentity, long generation, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Equals(authenticationTag, AuthenticationTag, StringComparison.Ordinal));

    public Task<CapabilityCatalogTrustState> AdvanceAsync(string workspaceIdentity, long expectedGeneration, string expectedContentDigest, long newGeneration, string newContentDigest, CancellationToken cancellationToken = default)
    {
        _state = new CapabilityCatalogTrustState(workspaceIdentity, newGeneration, newContentDigest, expectedGeneration, expectedContentDigest);
        return Task.FromResult(_state);
    }

    internal void SetCurrent(string workspaceIdentity, long generation, string contentDigest)
        => _state = new CapabilityCatalogTrustState(workspaceIdentity, generation, contentDigest, null, null);
}
