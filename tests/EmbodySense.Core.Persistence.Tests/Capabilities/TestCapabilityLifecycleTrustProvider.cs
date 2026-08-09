using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class TestCapabilityLifecycleTrustProvider : ICapabilityCatalogTrustProvider
{
    private readonly Dictionary<string, CapabilityCatalogTrustState> _states = new(StringComparer.Ordinal);

    internal Action<CancellationToken>? BeforeRead { get; set; }
    public int MaximumAuthenticationTagUtf8Bytes => 69;

    public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default)
    {
        BeforeRead?.Invoke(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_states.GetValueOrDefault(workspaceIdentity));
    }

    public Task<CapabilityCatalogTrustState> InitializeAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_states.TryGetValue(workspaceIdentity, out var state))
        {
            state = new CapabilityCatalogTrustState(workspaceIdentity, generation, contentDigest, null, null);
            _states.Add(workspaceIdentity, state);
        }
        return Task.FromResult(state);
    }

    public Task<string> AuthenticateArtifactAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Tag(workspaceIdentity, generation, contentDigest));
    }

    public Task<bool> VerifyArtifactAsync(string workspaceIdentity, long generation, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(authenticationTag == Tag(workspaceIdentity, generation, contentDigest));
    }

    public Task<CapabilityCatalogTrustState> AdvanceAsync(string workspaceIdentity, long expectedGeneration, string expectedContentDigest, long newGeneration, string newContentDigest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = _states[workspaceIdentity];
        if (current.CurrentGeneration != expectedGeneration || current.CurrentContentDigest != expectedContentDigest || newGeneration != expectedGeneration + 1)
        {
            throw new IOException("Test lifecycle trust compare-exchange conflict.");
        }
        var advanced = new CapabilityCatalogTrustState(workspaceIdentity, newGeneration, newContentDigest, expectedGeneration, expectedContentDigest);
        _states[workspaceIdentity] = advanced;
        return Task.FromResult(advanced);
    }

    private static string Tag(string workspaceIdentity, long generation, string contentDigest) => "test:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{workspaceIdentity}\n{generation}\n{contentDigest}"))).ToLowerInvariant();
}
