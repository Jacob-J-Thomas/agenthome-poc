using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal static class CapabilityLifecycleTestData
{
    internal static CapabilityDescriptor Descriptor(string version = "1.0.0")
    {
        var descriptor = CapabilityCatalogTestData.Descriptor("org.example/read-workspace");
        Assert.True(CapabilityVersion.TryParse(version, out var parsed, out _));
        return descriptor with { Version = parsed! };
    }

    internal static CapabilityIntegrityDigest Digest(string value) => CapabilityIntegrityDigest.Compute(System.Text.Encoding.UTF8.GetBytes(value));

    internal static CapabilityLifecycleBaseline Baseline()
    {
        var state = new CapabilityLifecycleState(Descriptor(), Digest("artifact-v1"), true, false, 1, "activate-v1", DateTimeOffset.Parse("2026-08-01T12:00:00Z"));
        return new CapabilityLifecycleBaseline(state, 7, 3);
    }

    internal static CapabilityDependent Dependent(string identity, CapabilityRequirementKind requirementKind, string range, CapabilityDependentKind kind = CapabilityDependentKind.Loop)
    {
        Assert.True(CapabilityId.TryParse("org.example/" + identity, out var subjectId, out _));
        Assert.True(CapabilityVersionRange.TryParse(range, out var parsedRange, out _));
        var dependency = new CapabilityDependency(Descriptor().Id, parsedRange!);
        var manifest = new CapabilityDependencyManifest(1, CapabilityDependencyManifestKind.LoopPackage, subjectId!, requirementKind == CapabilityRequirementKind.Required ? [dependency] : [], requirementKind == CapabilityRequirementKind.Optional ? [dependency] : [], new CapabilityDependencyArtifactMetadata(null, null));
        return new CapabilityDependent(kind, identity, "revision-1", manifest, kind == CapabilityDependentKind.Loop ? CapabilityAuthorityPosture.AssignedDefinition : CapabilityAuthorityPosture.MetadataOnly);
    }
}
