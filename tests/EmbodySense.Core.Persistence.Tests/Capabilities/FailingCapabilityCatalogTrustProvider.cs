using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class FailingCapabilityCatalogTrustProvider(ICapabilityCatalogTrustProvider inner) : ICapabilityCatalogTrustProvider
{
    public bool FailAfterNextInitialization { get; set; }

    public bool FailNextAdvance { get; set; }

    public long? FailAuthenticationGeneration { get; set; }

    public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default) => inner.ReadAsync(workspaceIdentity, cancellationToken);

    public async Task<CapabilityCatalogTrustState> InitializeAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
    {
        var initialized = await inner.InitializeAsync(workspaceIdentity, generation, contentDigest, cancellationToken);
        if (FailAfterNextInitialization)
        {
            FailAfterNextInitialization = false;
            throw new IOException("Injected crash after trust initialization.");
        }

        return initialized;
    }

    public Task<string> AuthenticateArtifactAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
    {
        if (FailAuthenticationGeneration == generation)
        {
            FailAuthenticationGeneration = null;
            throw new IOException("Injected crash before successor artifact authentication.");
        }

        return inner.AuthenticateArtifactAsync(workspaceIdentity, generation, contentDigest, cancellationToken);
    }

    public Task<bool> VerifyArtifactAsync(string workspaceIdentity, long generation, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default) => inner.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, authenticationTag, cancellationToken);

    public async Task<CapabilityCatalogTrustState> AdvanceAsync(string workspaceIdentity, long expectedGeneration, string expectedContentDigest, long newGeneration, string newContentDigest, CancellationToken cancellationToken = default)
    {
        if (FailNextAdvance)
        {
            FailNextAdvance = false;
            throw new IOException("Injected crash before trust advancement.");
        }

        return await inner.AdvanceAsync(workspaceIdentity, expectedGeneration, expectedContentDigest, newGeneration, newContentDigest, cancellationToken);
    }
}
