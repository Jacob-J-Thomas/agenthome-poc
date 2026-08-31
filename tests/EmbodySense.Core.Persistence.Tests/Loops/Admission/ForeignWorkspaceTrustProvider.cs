using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Loops.Admission;

internal sealed class ForeignWorkspaceTrustProvider(
    ICapabilityCatalogTrustProvider inner,
    bool substituteRead = false,
    bool substituteInitialize = false) : ICapabilityCatalogTrustProvider
{
    public int MaximumAuthenticationTagUtf8Bytes => inner.MaximumAuthenticationTagUtf8Bytes;

    public void RequireDisjointWorkspace(string workspaceRootPath) => inner.RequireDisjointWorkspace(workspaceRootPath);

    public async Task<CapabilityCatalogTrustState?> ReadAsync(
        string workspaceIdentity,
        CancellationToken cancellationToken = default)
    {
        var state = await inner.ReadAsync(workspaceIdentity, cancellationToken);
        return state is not null && substituteRead
            ? state with { WorkspaceIdentity = "sha256:" + new string('f', 64) }
            : state;
    }

    public async Task<CapabilityCatalogTrustState> InitializeAsync(
        string workspaceIdentity,
        long generation,
        string contentDigest,
        CancellationToken cancellationToken = default)
    {
        var state = await inner.InitializeAsync(workspaceIdentity, generation, contentDigest, cancellationToken);
        return substituteInitialize
            ? state with { WorkspaceIdentity = "sha256:" + new string('f', 64) }
            : state;
    }

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

    public Task<CapabilityCatalogTrustState> AdvanceAsync(
        string workspaceIdentity,
        long expectedGeneration,
        string expectedContentDigest,
        long newGeneration,
        string newContentDigest,
        CancellationToken cancellationToken = default)
        => inner.AdvanceAsync(
            workspaceIdentity,
            expectedGeneration,
            expectedContentDigest,
            newGeneration,
            newContentDigest,
            cancellationToken);
}
