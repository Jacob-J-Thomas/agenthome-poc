using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Stores artifact trust anchors beneath one server-owned root by domain-separating the catalog trust primitive.</summary>
public sealed class FileCapabilityArtifactStateTrustProvider : ICapabilityArtifactStateTrustProvider
{
    private readonly ICapabilityCatalogTrustProvider _provider;

    /// <summary>Creates a server-owned artifact trust provider.</summary>
    public FileCapabilityArtifactStateTrustProvider(string rootPath, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null) => _provider = new FileCapabilityCatalogTrustProvider(rootPath, durabilityBarrier);

    /// <inheritdoc />
    public async Task<string> AuthenticateStagedEvidenceAsync(string workspaceIdentity, string artifactDigest, string evidenceDigest, CancellationToken cancellationToken = default)
    {
        var identity = Identity(workspaceIdentity, "staged", artifactDigest);
        var state = await _provider.ReadAsync(identity, cancellationToken) ?? await _provider.InitializeAsync(identity, 0, evidenceDigest, cancellationToken);
        if (state.CurrentGeneration != 0 || state.CurrentContentDigest != evidenceDigest)
        {
            throw new IOException("Server-owned staged-artifact trust evidence conflicts with an existing immutable artifact.");
        }
        return await _provider.AuthenticateArtifactAsync(identity, 0, evidenceDigest, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> VerifyStagedEvidenceAsync(string workspaceIdentity, string artifactDigest, string evidenceDigest, string authenticationTag, CancellationToken cancellationToken = default) => _provider.VerifyArtifactAsync(Identity(workspaceIdentity, "staged", artifactDigest), 0, evidenceDigest, authenticationTag, cancellationToken);

    /// <inheritdoc />
    public async Task<CapabilityArtifactTrustState?> ReadActivationAsync(string workspaceIdentity, CancellationToken cancellationToken = default)
    {
        var state = await _provider.ReadAsync(Identity(workspaceIdentity, "activation", null), cancellationToken);
        return state is null ? null : Map(state);
    }

    /// <inheritdoc />
    public async Task<CapabilityArtifactTrustState> InitializeActivationAsync(string workspaceIdentity, string contentDigest, CancellationToken cancellationToken = default) => Map(await _provider.InitializeAsync(Identity(workspaceIdentity, "activation", null), 0, contentDigest, cancellationToken));

    /// <inheritdoc />
    public Task<string> AuthenticateActivationAsync(string workspaceIdentity, long revision, string contentDigest, CancellationToken cancellationToken = default) => _provider.AuthenticateArtifactAsync(Identity(workspaceIdentity, "activation", null), revision, contentDigest, cancellationToken);

    /// <inheritdoc />
    public Task<bool> VerifyActivationAsync(string workspaceIdentity, long revision, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default) => _provider.VerifyArtifactAsync(Identity(workspaceIdentity, "activation", null), revision, contentDigest, authenticationTag, cancellationToken);

    /// <inheritdoc />
    public async Task<CapabilityArtifactTrustState> AdvanceActivationAsync(string workspaceIdentity, long expectedRevision, string expectedContentDigest, long newRevision, string newContentDigest, CancellationToken cancellationToken = default) => Map(await _provider.AdvanceAsync(Identity(workspaceIdentity, "activation", null), expectedRevision, expectedContentDigest, newRevision, newContentDigest, cancellationToken));

    private static string Identity(string workspaceIdentity, string purpose, string? artifactDigest)
    {
        var material = $"embodysense-capability-artifact-trust-v1\n{workspaceIdentity}\n{purpose}\n{artifactDigest}";
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static CapabilityArtifactTrustState Map(CapabilityCatalogTrustState state) => new(state.CurrentGeneration, state.CurrentContentDigest, state.PreviousGeneration, state.PreviousContentDigest);
}
