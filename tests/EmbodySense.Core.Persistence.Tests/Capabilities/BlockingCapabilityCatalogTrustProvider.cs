using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class BlockingCapabilityCatalogTrustProvider(ICapabilityCatalogTrustProvider inner) : ICapabilityCatalogTrustProvider
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool BlockNextRead { get; set; }

    public Task Entered => _entered.Task;

    public void Release() => _release.TrySetResult();

    public void RequireDisjointWorkspace(string workspaceRootPath) => inner.RequireDisjointWorkspace(workspaceRootPath);

    public async Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default)
    {
        if (BlockNextRead)
        {
            BlockNextRead = false;
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        return await inner.ReadAsync(workspaceIdentity, cancellationToken);
    }

    public Task<CapabilityCatalogTrustState> InitializeAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default) => inner.InitializeAsync(workspaceIdentity, generation, contentDigest, cancellationToken);

    public Task<string> AuthenticateArtifactAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default) => inner.AuthenticateArtifactAsync(workspaceIdentity, generation, contentDigest, cancellationToken);

    public Task<bool> VerifyArtifactAsync(string workspaceIdentity, long generation, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default) => inner.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, authenticationTag, cancellationToken);

    public Task<CapabilityCatalogTrustState> AdvanceAsync(string workspaceIdentity, long expectedGeneration, string expectedContentDigest, long newGeneration, string newContentDigest, CancellationToken cancellationToken = default) => inner.AdvanceAsync(workspaceIdentity, expectedGeneration, expectedContentDigest, newGeneration, newContentDigest, cancellationToken);
}
