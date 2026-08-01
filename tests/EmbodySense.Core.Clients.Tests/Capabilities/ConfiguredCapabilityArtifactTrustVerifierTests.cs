using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Clients.Capabilities;

namespace EmbodySense.Core.Clients.Tests.Capabilities;

public sealed class ConfiguredCapabilityArtifactTrustVerifierTests
{
    [Fact]
    public async Task Server_configured_digest_pin_verifies_unsigned_exact_bytes_only()
    {
        var manifest = CapabilityClientTestData.Manifest();
        using var verifier = new ConfiguredCapabilityArtifactTrustVerifier(new Dictionary<string, string>(), [EmbodySense.Core.Application.Capabilities.CapabilityArtifactManifestCanonicalizer.ComputePolicyPin(manifest).Value]);

        var verified = await verifier.VerifyAsync(manifest, manifest.Checksum);
        var other = await verifier.VerifyAsync(manifest, EmbodySense.Core.Common.Capabilities.CapabilityIntegrityDigest.Compute("other"u8));

        Assert.Equal(CapabilityArtifactTrustStatus.Verified, verified.Status);
        Assert.Equal(CapabilityArtifactTrustStatus.Rejected, other.Status);
    }

    [Fact]
    public async Task Ecdsa_signature_is_verified_by_server_key_not_manifest_claim()
    {
        var manifest = CapabilityClientTestData.Manifest();
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(EmbodySense.Core.Application.Capabilities.CapabilityArtifactManifestCanonicalizer.TryGetSignaturePayload(manifest, out var payload));
        var signature = signingKey.SignData(payload, HashAlgorithmName.SHA256);
        var signed = manifest with { Signature = new CapabilityArtifactSignatureEvidence("ecdsa-p256-sha256", "trusted-key", Convert.ToBase64String(signature)) };
        using var verifier = new ConfiguredCapabilityArtifactTrustVerifier(new Dictionary<string, string> { ["trusted-key"] = signingKey.ExportSubjectPublicKeyInfoPem() });

        var verified = await verifier.VerifyAsync(signed, manifest.Checksum);
        var forged = await verifier.VerifyAsync(signed with { Signature = signed.Signature! with { Signature = Convert.ToBase64String(new byte[signature.Length]) } }, manifest.Checksum);

        Assert.Equal(CapabilityArtifactTrustStatus.Verified, verified.Status);
        Assert.Equal(CapabilityArtifactTrustStatus.Rejected, forged.Status);
    }

    [Fact]
    public async Task Signed_or_pinned_artifact_cannot_be_relabelled()
    {
        var manifest = CapabilityClientTestData.Manifest();
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(EmbodySense.Core.Application.Capabilities.CapabilityArtifactManifestCanonicalizer.TryGetSignaturePayload(manifest, out var payload));
        var signed = manifest with { Signature = new CapabilityArtifactSignatureEvidence("ecdsa-p256-sha256", "trusted-key", Convert.ToBase64String(signingKey.SignData(payload, HashAlgorithmName.SHA256))) };
        using var verifier = new ConfiguredCapabilityArtifactTrustVerifier(new Dictionary<string, string> { ["trusted-key"] = signingKey.ExportSubjectPublicKeyInfoPem() }, [EmbodySense.Core.Application.Capabilities.CapabilityArtifactManifestCanonicalizer.ComputePolicyPin(manifest).Value]);
        var relabelled = signed with { Source = signed.Source with { Uri = "file:///different/echo.exe" }, EntryPoint = "other.exe" };

        Assert.Equal(CapabilityArtifactTrustStatus.Rejected, (await verifier.VerifyAsync(relabelled, relabelled.Checksum)).Status);
        Assert.Equal(CapabilityArtifactTrustStatus.Rejected, (await verifier.VerifyAsync(manifest with { EntryPoint = "other.exe" }, manifest.Checksum)).Status);
    }

    [Fact]
    public async Task Unknown_signature_key_fails_unavailable()
    {
        var manifest = CapabilityClientTestData.Manifest() with { Signature = new CapabilityArtifactSignatureEvidence("ecdsa-p256-sha256", "missing", Convert.ToBase64String(new byte[64])) };
        using var verifier = new ConfiguredCapabilityArtifactTrustVerifier(new Dictionary<string, string>());

        Assert.Equal(CapabilityArtifactTrustStatus.Unavailable, (await verifier.VerifyAsync(manifest, manifest.Checksum)).Status);
    }

    [Fact]
    public async Task Malformed_signature_and_cancellation_are_rejected_before_crypto_work()
    {
        var manifest = CapabilityClientTestData.Manifest() with { Signature = new CapabilityArtifactSignatureEvidence("ecdsa-p256-sha256", "trusted-key", "not-base64") };
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new ConfiguredCapabilityArtifactTrustVerifier(new Dictionary<string, string> { ["trusted-key"] = signingKey.ExportSubjectPublicKeyInfoPem() });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(CapabilityArtifactTrustStatus.Rejected, (await verifier.VerifyAsync(manifest, manifest.Checksum)).Status);
        await Assert.ThrowsAsync<OperationCanceledException>(() => verifier.VerifyAsync(manifest, manifest.Checksum, cancellation.Token));
    }

    [Fact]
    public void Invalid_server_configuration_fails_closed()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.Throws<ArgumentNullException>(() => new ConfiguredCapabilityArtifactTrustVerifier(null!));
        Assert.Throws<ArgumentException>(() => new ConfiguredCapabilityArtifactTrustVerifier(new Dictionary<string, string> { ["not valid"] = p384.ExportSubjectPublicKeyInfoPem() }));
        Assert.Throws<ArgumentException>(() => new ConfiguredCapabilityArtifactTrustVerifier(new Dictionary<string, string> { ["p384"] = p384.ExportSubjectPublicKeyInfoPem() }));
        Assert.Throws<ArgumentException>(() => new ConfiguredCapabilityArtifactTrustVerifier(new Dictionary<string, string>(), ["not-a-digest"]));
    }

    [Fact]
    public void Later_invalid_key_rejects_configuration_after_a_valid_key_was_imported()
    {
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.Throws<ArgumentException>(() => new ConfiguredCapabilityArtifactTrustVerifier(new Dictionary<string, string>
        {
            ["valid-key"] = p256.ExportSubjectPublicKeyInfoPem(),
            ["invalid-key"] = p384.ExportSubjectPublicKeyInfoPem()
        }));
    }
}
