using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal static class CapabilityArtifactTestData
{
    internal static readonly byte[] Content = "artifact-content"u8.ToArray();

    internal static CapabilityPlatform Platform(string value = "windows/x64")
    {
        Assert.True(CapabilityPlatform.TryParse(value, out var platform, out var error), error?.Message);
        return platform!;
    }

    internal static CapabilityVersion Version(string value = "1.0.0")
    {
        Assert.True(CapabilityVersion.TryParse(value, out var version, out var error), error?.Message);
        return version!;
    }

    internal static CapabilityArtifactManifest Manifest(CapabilityArtifactSourceKind kind = CapabilityArtifactSourceKind.Local, byte[]? content = null, bool secrets = false)
    {
        content ??= Content;
        var digest = CapabilityIntegrityDigest.Compute(content);
        Assert.True(CapabilityId.TryParse("org.example/echo", out var id, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var range, out _));
        Assert.True(CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _));
        var uri = kind == CapabilityArtifactSourceKind.Local ? "file:///sources/echo.exe" : "https://example.test/echo.exe";
        var provenanceKind = kind == CapabilityArtifactSourceKind.Local ? CapabilityProvenanceKind.LocalSource : CapabilityProvenanceKind.RemoteArtifact;
        Assert.True(CapabilitySecretRequirement.TryParse("api_token", out var secret, out _));
        var requirements = new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], secrets ? [secret!] : []);
        var descriptor = new CapabilityDescriptor(1, id!, CapabilityKind.Skill, Version(), new CapabilityImplementationIdentity(provider!, "echo"), new CapabilityProvenance(provenanceKind, uri, "rev-1", digest), new CapabilityCompatibility(range!, [Platform()]), "Echo test artifact.", schema!, schema!, new CapabilityResourceLimits(1_000, 32_000_000, 16_384, 1), CapabilitySideEffectClass.None, requirements);
        return new CapabilityArtifactManifest(1, descriptor, new CapabilityArtifactSourceReference(kind, uri, "rev-1", CapabilityArtifactUpdatePolicy.Pinned), digest, null, Platform(), "echo.exe", []);
    }
}
