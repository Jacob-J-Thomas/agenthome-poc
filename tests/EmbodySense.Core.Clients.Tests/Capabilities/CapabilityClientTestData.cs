using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Clients.Tests.Capabilities;

internal static class CapabilityClientTestData
{
    internal static CapabilityArtifactManifest Manifest(string entryPoint = "artifact.exe", string behavior = "echo", int milliseconds = 5_000, int outputBytes = 16_384, bool secrets = false)
    {
        var digest = CapabilityIntegrityDigest.Compute("artifact"u8);
        Assert.True(CapabilityId.TryParse("org.example/echo", out var id, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var range, out _));
        Assert.True(CapabilityPlatform.TryParse("windows/x64", out var platform, out _));
        Assert.True(CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _));
        Assert.True(CapabilitySecretRequirement.TryParse("api_token", out var secret, out _));
        var uri = "file:///sources/artifact.exe";
        var descriptor = new CapabilityDescriptor(1, id!, CapabilityKind.Skill, version!, new CapabilityImplementationIdentity(provider!, "echo"), new CapabilityProvenance(CapabilityProvenanceKind.LocalSource, uri, "rev-1", digest), new CapabilityCompatibility(range!, [platform!]), "Echo test artifact.", schema!, schema!, new CapabilityResourceLimits(milliseconds, 32_000_000, outputBytes, 1), CapabilitySideEffectClass.None, new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], secrets ? [secret!] : []));
        return new CapabilityArtifactManifest(1, descriptor, new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Local, uri, "rev-1", CapabilityArtifactUpdatePolicy.Pinned), digest, null, platform!, entryPoint, ["capability", behavior]);
    }
}
