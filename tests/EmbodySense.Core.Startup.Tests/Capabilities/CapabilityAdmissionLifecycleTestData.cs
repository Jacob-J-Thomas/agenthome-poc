using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Startup.Capabilities;

namespace EmbodySense.Core.Startup.Tests.Capabilities;

internal static class CapabilityAdmissionLifecycleTestData
{
    internal static readonly byte[] Content = "runtime-lifecycle-artifact"u8.ToArray();

    internal static CapabilityArtifactStageRequest Stage()
    {
        var digest = CapabilityIntegrityDigest.Compute(Content);
        Assert.True(CapabilityId.TryParse("org.example/runtime-lifecycle", out var id, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var range, out _));
        Assert.True(CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _));
        const string SourceUri = "file:///sources/runtime-lifecycle";
        var descriptor = new CapabilityDescriptor(1, id!, CapabilityKind.Skill, version!, new CapabilityImplementationIdentity(provider!, "runtime-lifecycle"), new CapabilityProvenance(CapabilityProvenanceKind.LocalSource, SourceUri, "rev-1", digest), new CapabilityCompatibility(range!, [CapabilityHostRuntime.Platform]), "Runtime lifecycle admission test capability.", schema!, schema!, new CapabilityResourceLimits(1_000, 32_000_000, 16_384, 1), CapabilitySideEffectClass.None, new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], []));
        var manifest = new CapabilityArtifactManifest(1, descriptor, new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Local, SourceUri, "rev-1", CapabilityArtifactUpdatePolicy.Pinned), digest, null, CapabilityHostRuntime.Platform, "runtime-lifecycle", []);
        return new CapabilityArtifactStageRequest(manifest, new CapabilityArtifactContent(Content), new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Verified, "test-server-policy", "Verified."));
    }

    internal static CapabilityDependencyManifest Requirements(CapabilityId capabilityId)
    {
        Assert.True(CapabilityId.TryParse("org.example/runtime-loop", out var subject, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var range, out _));
        return new CapabilityDependencyManifest(1, CapabilityDependencyManifestKind.LoopPackage, subject!, [new CapabilityDependency(capabilityId, range!)], [], new CapabilityDependencyArtifactMetadata(null, null));
    }
}
