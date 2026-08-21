using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Tests.Support;

public static class TestCapabilityAdmissionFactory
{
    public static CapabilityAdmissionSnapshot Create(CapabilityDependencyManifest requirements, DateTimeOffset? admittedAtUtc = null)
    {
        _ = CapabilityDependencyManifestHash.TryCompute(requirements, out var requirementsHash, out _);
        _ = CapabilityProviderId.TryParse("org.embodysense", out var provider, out _);
        _ = CapabilityVersion.TryParse("1.0.0", out var version, out _);
        var pins = requirements.Required.Select(dependency =>
        {
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dependency.CapabilityId.Value))).ToLowerInvariant();
            _ = CapabilityDescriptorHash.TryParse("sha256:" + digest, out var descriptorHash, out _);
            var implementationId = dependency.CapabilityId.Value[(dependency.CapabilityId.Value.IndexOf('/') + 1)..];
            var kind = implementationId switch
            {
                "workspace-command" => CapabilityKind.Actuator,
                _ when implementationId.StartsWith("model-profile/", StringComparison.Ordinal) => CapabilityKind.ModelProfile,
                _ => CapabilityKind.GraphNode,
            };
            return new CapabilityAdmissionPin(
                new CapabilityDescriptorIdentity(dependency.CapabilityId, version!, descriptorHash!),
                kind,
                new CapabilityImplementationIdentity(provider!, implementationId),
                new CapabilityProvenance(CapabilityProvenanceKind.BuiltIn, "https://embodysense.dev/builtins/" + implementationId, "1", null),
                new CapabilityDependencyArtifactMetadata(null, null),
                "Test-safe description for " + implementationId + ".");
        }).ToArray();
        var evidence = requirements.Required.Select(dependency =>
        {
            var pin = pins.Single(item => item.DescriptorIdentity.Id.Equals(dependency.CapabilityId));
            return new CapabilityAdmissionEvidence(requirements.SubjectId, dependency.CapabilityId, dependency.CompatibleVersionRange, false, "Selected", pin.DescriptorIdentity, "Selected exact test capability pin.");
        }).ToArray();
        return new CapabilityAdmissionSnapshot(1, "workspace-sha256:" + new string('1', 64), requirements, requirementsHash!.Value, pins, evidence, admittedAtUtc ?? new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
    }
}
