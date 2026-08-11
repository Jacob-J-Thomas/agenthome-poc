using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Authority;

internal sealed class UnexpectedAdvanceTrustProvider(ICapabilityCatalogTrustProvider inner, UnexpectedAdvanceMode mode) : ICapabilityCatalogTrustProvider
{
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
        var prior = await inner.ReadAsync(workspaceIdentity, cancellationToken)
            ?? new CapabilityCatalogTrustState(workspaceIdentity, expectedGeneration, expectedContentDigest, null, null);
        if (mode == UnexpectedAdvanceMode.NoOp)
        {
            return prior;
        }

        var advanced = await inner.AdvanceAsync(workspaceIdentity, expectedGeneration, expectedContentDigest, newGeneration, newContentDigest, cancellationToken);
        return mode switch
        {
            UnexpectedAdvanceMode.Stale => prior,
            UnexpectedAdvanceMode.WrongWorkspace => advanced with { WorkspaceIdentity = workspaceIdentity + "-substituted" },
            UnexpectedAdvanceMode.WrongCurrentGeneration => advanced with { CurrentGeneration = checked(newGeneration + 1) },
            UnexpectedAdvanceMode.WrongCurrentDigest => advanced with { CurrentContentDigest = new('f', 64) },
            UnexpectedAdvanceMode.WrongPreviousGeneration => advanced with { PreviousGeneration = checked(expectedGeneration + 1) },
            UnexpectedAdvanceMode.WrongPreviousDigest => advanced with { PreviousContentDigest = new('e', 64) },
            _ => throw new InvalidOperationException("The injected advance-result mode is unsupported.")
        };
    }
}
