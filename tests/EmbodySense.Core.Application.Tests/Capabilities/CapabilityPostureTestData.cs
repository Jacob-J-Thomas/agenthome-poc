using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal static class CapabilityPostureTestData
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-01T12:00:00Z");

    internal static CapabilityCatalogEntry Entry(
        CapabilityDescriptor? descriptor = null,
        CapabilityEnablementState enablement = CapabilityEnablementState.Enabled,
        CapabilityHealthState health = CapabilityHealthState.Healthy,
        CapabilityRetirementState retirement = CapabilityRetirementState.Active,
        CapabilityTrustState trust = CapabilityTrustState.Verified)
    {
        descriptor ??= CapabilityArtifactTestData.Manifest(secrets: true).Descriptor;
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var error), error is null ? null : string.Join(", ", error.Errors.Select(item => item.Message)));
        var lifecycle = new CapabilityLifecycleSnapshot(
            CapabilityLifecycleSnapshot.CurrentSchemaVersion,
            identity!,
            CapabilityDeclarationState.Declared,
            CapabilityInstallationState.Installed,
            enablement,
            health,
            retirement,
            trust);
        return new CapabilityCatalogEntry(descriptor, lifecycle, 7, Now, "test-operation");
    }

    internal static CapabilityDependent Dependent(CapabilityId capabilityId, CapabilityRequirementKind requirementKind, string compatibleVersionRange, int index = 0)
    {
        Assert.True(CapabilityId.TryParse($"org.example/loop-{index:D3}", out var subjectId, out _));
        Assert.True(CapabilityVersionRange.TryParse(compatibleVersionRange, out var range, out _));
        var dependency = new CapabilityDependency(capabilityId, range!);
        var required = requirementKind == CapabilityRequirementKind.Required ? new[] { dependency } : [];
        var optional = requirementKind == CapabilityRequirementKind.Optional ? new[] { dependency } : [];
        var manifest = new CapabilityDependencyManifest(CapabilityDependencyManifest.CurrentSchemaVersion, CapabilityDependencyManifestKind.LoopPackage, subjectId!, required, optional, new CapabilityDependencyArtifactMetadata(null, null));
        return new CapabilityDependent(CapabilityDependentKind.Loop, $"loop-{index:D3}", $"revision-{index:D3}", manifest, CapabilityAuthorityPosture.AssignedDefinition);
    }

    internal static CapabilityLifecycleReadResult Lifecycle(CapabilityCatalogEntry entry, CapabilityLifecycleReadStatus status = CapabilityLifecycleReadStatus.Available)
    {
        var state = new CapabilityLifecycleState(entry.Descriptor, CapabilityIntegrityDigest.Compute("current"u8), true, entry.Lifecycle.Retirement == CapabilityRetirementState.Removed, 7, "test-lifecycle", Now);
        return new CapabilityLifecycleReadResult(status, state, [], [], 7, "proved");
    }

    internal static CapabilityAdmissionSnapshot Admission(params string[] capabilityIds)
    {
        Assert.True(CapabilityId.TryParse("org.example/model-loop", out var subjectId, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var range, out _));
        var dependencies = capabilityIds.Select(value =>
        {
            Assert.True(CapabilityId.TryParse(value, out var id, out _));
            return new CapabilityDependency(id!, range!);
        }).ToArray();
        var manifest = new CapabilityDependencyManifest(CapabilityDependencyManifest.CurrentSchemaVersion, CapabilityDependencyManifestKind.LoopPackage, subjectId!, dependencies, [], new CapabilityDependencyArtifactMetadata(null, null));
        return TestCapabilityAdmissionFactory.Create(manifest);
    }

    internal static CapabilityDescriptor WithCompatibility(CapabilityDescriptor descriptor, string versionRange, string platform)
    {
        Assert.True(CapabilityVersionRange.TryParse(versionRange, out var parsedRange, out _));
        Assert.True(CapabilityPlatform.TryParse(platform, out var parsedPlatform, out _));
        return descriptor with { Compatibility = new CapabilityCompatibility(parsedRange!, [parsedPlatform!]) };
    }

    internal static CapabilityVersion Version(string value)
    {
        Assert.True(CapabilityVersion.TryParse(value, out var version, out _));
        return version!;
    }

    internal static CapabilityPlatform Platform(string value)
    {
        Assert.True(CapabilityPlatform.TryParse(value, out var platform, out _));
        return platform!;
    }
}
