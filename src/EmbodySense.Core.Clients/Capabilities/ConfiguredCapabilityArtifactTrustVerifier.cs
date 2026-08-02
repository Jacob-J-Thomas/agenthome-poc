using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Clients.Capabilities;

/// <summary>Verifies artifact evidence against server-configured digest allowlists and ECDSA P-256 public keys.</summary>
/// <remarks>Signatures and server pins cover the canonical manifest envelope, including provenance and executable semantics.</remarks>
public sealed class ConfiguredCapabilityArtifactTrustVerifier : ICapabilityArtifactTrustVerifier, IDisposable
{
    private readonly IReadOnlyDictionary<string, ECDsa> _keys;
    private readonly IReadOnlySet<string> _trustedUnsignedDigests;

    /// <summary>Creates a verifier from server-owned PEM public keys and optional exact unsigned manifest-policy pins.</summary>
    public ConfiguredCapabilityArtifactTrustVerifier(IReadOnlyDictionary<string, string> publicKeyPemById, IEnumerable<string>? trustedUnsignedManifestPins = null)
    {
        ArgumentNullException.ThrowIfNull(publicKeyPemById);
        var keys = new Dictionary<string, ECDsa>(StringComparer.Ordinal);
        try
        {
            foreach (var pair in publicKeyPemById)
            {
                if (!CapabilityArtifactManifestValidator.IsOperationId(pair.Key))
                {
                    throw new ArgumentException("Verification key identifiers must be bounded canonical tokens.", nameof(publicKeyPemById));
                }
                var key = ECDsa.Create();
                key.ImportFromPem(pair.Value);
                if (key.KeySize != 256)
                {
                    key.Dispose();
                    throw new ArgumentException("Only ECDSA P-256 verification keys are supported.", nameof(publicKeyPemById));
                }
                keys.Add(pair.Key, key);
            }
        }
        catch
        {
            foreach (var key in keys.Values)
            {
                key.Dispose();
            }
            throw;
        }

        _keys = keys;
        _trustedUnsignedDigests = new HashSet<string>(trustedUnsignedManifestPins ?? [], StringComparer.Ordinal);
        if (_trustedUnsignedDigests.Any(value => !CapabilityIntegrityDigest.TryParse(value, out _, out _)))
        {
            Dispose();
            throw new ArgumentException("Unsigned trust pins must be canonical SHA-256 manifest-policy pins.", nameof(trustedUnsignedManifestPins));
        }
    }

    /// <inheritdoc />
    public Task<CapabilityArtifactTrustDecision> VerifyAsync(CapabilityArtifactManifest manifest, CapabilityIntegrityDigest actualDigest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(actualDigest);
        cancellationToken.ThrowIfCancellationRequested();
        if (!manifest.Checksum.FixedTimeEquals(actualDigest))
        {
            return Task.FromResult(new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Rejected, "server-artifact-envelope-v1", "The verified bytes do not match the manifest checksum."));
        }
        if (!CapabilityArtifactManifestCanonicalizer.TryGetSignaturePayload(manifest, out var payload))
        {
            return Task.FromResult(new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Rejected, "server-artifact-envelope-v1", "The artifact manifest cannot be authenticated."));
        }
        if (manifest.Signature is null)
        {
            var trusted = _trustedUnsignedDigests.Contains(CapabilityIntegrityDigest.Compute(payload).Value);
            return Task.FromResult(new CapabilityArtifactTrustDecision(trusted ? CapabilityArtifactTrustStatus.Verified : CapabilityArtifactTrustStatus.Rejected, "server-artifact-envelope-pins-v1", trusted ? "The exact unsigned artifact envelope is trusted by server-owned policy." : "Unsigned artifact envelope is not trusted by server-owned policy."));
        }

        if (!_keys.TryGetValue(manifest.Signature.KeyId, out var key))
        {
            return Task.FromResult(new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Unavailable, "server-ecdsa-p256-v1", "The configured verification key is unavailable."));
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(manifest.Signature.Signature);
        }
        catch (FormatException)
        {
            return Task.FromResult(new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Rejected, "server-ecdsa-p256-v1", "The artifact signature evidence is malformed."));
        }
        var verified = key.VerifyData(payload, signature, HashAlgorithmName.SHA256);
        return Task.FromResult(new CapabilityArtifactTrustDecision(verified ? CapabilityArtifactTrustStatus.Verified : CapabilityArtifactTrustStatus.Rejected, "server-ecdsa-p256-v1", verified ? "The artifact signature was verified by server-owned policy." : "The artifact signature did not verify."));
    }

    /// <summary>Releases imported public-key handles.</summary>
    public void Dispose()
    {
        foreach (var key in _keys.Values)
        {
            key.Dispose();
        }
    }
}
