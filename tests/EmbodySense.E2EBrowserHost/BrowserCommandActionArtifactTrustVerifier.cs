using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.E2EBrowserHost;

/// <summary>Recognizes only artifacts already supplied by the controlled browser test fixture.</summary>
public sealed class BrowserCommandActionArtifactTrustVerifier : ICapabilityArtifactTrustVerifier
{
    /// <summary>Gets the singleton browser-test artifact verifier.</summary>
    public static BrowserCommandActionArtifactTrustVerifier Instance { get; } = new();

    private BrowserCommandActionArtifactTrustVerifier()
    {
    }

    /// <inheritdoc />
    public Task<CapabilityArtifactTrustDecision> VerifyAsync(CapabilityArtifactManifest manifest, CapabilityIntegrityDigest actualDigest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(actualDigest);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(manifest.Checksum.FixedTimeEquals(actualDigest)
            ? new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Verified, "browser-e2e-policy", "The exact fixture artifact is trusted.")
            : new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Rejected, "browser-e2e-policy", "The fixture artifact digest changed."));
    }
}
