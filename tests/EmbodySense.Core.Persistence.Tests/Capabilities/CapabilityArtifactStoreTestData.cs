using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal static class CapabilityArtifactStoreTestData
{
    internal static CapabilityArtifactManifest Manifest(byte[] content, string version = "1.0.0")
    {
        var digest = CapabilityIntegrityDigest.Compute(content);
        Assert.True(CapabilityId.TryParse("org.example/echo", out var id, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out _));
        Assert.True(CapabilityVersion.TryParse(version, out var parsedVersion, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var range, out _));
        Assert.True(CapabilityPlatform.TryParse("windows/x64", out var platform, out _));
        Assert.True(CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _));
        var sourceUri = $"file:///sources/echo-{version}.exe";
        var descriptor = new CapabilityDescriptor(1, id!, CapabilityKind.Skill, parsedVersion!, new CapabilityImplementationIdentity(provider!, "echo"), new CapabilityProvenance(CapabilityProvenanceKind.LocalSource, sourceUri, "rev-" + version, digest), new CapabilityCompatibility(range!, [platform!]), "Echo test artifact.", schema!, schema!, new CapabilityResourceLimits(1_000, 32_000_000, 16_384, 1), CapabilitySideEffectClass.None, new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], []));
        return new CapabilityArtifactManifest(1, descriptor, new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Local, sourceUri, "rev-" + version, CapabilityArtifactUpdatePolicy.Pinned), digest, null, platform!, "echo.exe", []);
    }

    internal static CapabilityArtifactStageRequest Stage(byte[] content, string version = "1.0.0") => new(Manifest(content, version), new CapabilityArtifactContent(content), new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Verified, "test-server-policy", "Verified."));
}
