using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Requests;

internal sealed class HumanInputMalformedAdvanceTrustProvider(ICapabilityCatalogTrustProvider inner) : ICapabilityCatalogTrustProvider
{
    private const string WrongDigest = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    public int MaximumAuthenticationTagUtf8Bytes => inner.MaximumAuthenticationTagUtf8Bytes;

    public void RequireDisjointWorkspace(string workspaceRootPath) => inner.RequireDisjointWorkspace(workspaceRootPath);

    public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default)
        => inner.ReadAsync(workspaceIdentity, cancellationToken);

    public Task<CapabilityCatalogTrustState> InitializeAsync(
        string workspaceIdentity,
        long generation,
        string contentDigest,
        CancellationToken cancellationToken = default)
        => inner.InitializeAsync(workspaceIdentity, generation, contentDigest, cancellationToken);

    public Task<string> AuthenticateArtifactAsync(
        string workspaceIdentity,
        long generation,
        string contentDigest,
        CancellationToken cancellationToken = default)
        => inner.AuthenticateArtifactAsync(workspaceIdentity, generation, contentDigest, cancellationToken);

    public Task<bool> VerifyArtifactAsync(
        string workspaceIdentity,
        long generation,
        string contentDigest,
        string authenticationTag,
        CancellationToken cancellationToken = default)
        => inner.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, authenticationTag, cancellationToken);

    public async Task<CapabilityCatalogTrustState> AdvanceAsync(
        string workspaceIdentity,
        long expectedGeneration,
        string expectedContentDigest,
        long newGeneration,
        string newContentDigest,
        CancellationToken cancellationToken = default)
    {
        var advanced = await inner.AdvanceAsync(
            workspaceIdentity,
            expectedGeneration,
            expectedContentDigest,
            newGeneration,
            newContentDigest,
            cancellationToken);
        return advanced with { CurrentContentDigest = WrongDigest };
    }
}
