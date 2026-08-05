using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal static class CapabilityCatalogTestData
{
    internal static CapabilityDescriptor Descriptor(string idValue = "org.example/read-workspace", string secretReference = "provider-token")
    {
        _ = CapabilityId.TryParse(idValue, out var id, out _);
        _ = CapabilityProviderId.TryParse("org.example", out var provider, out _);
        _ = CapabilityVersion.TryParse("1.0.0", out var version, out _);
        _ = CapabilityVersionRange.TryParse("*", out var range, out _);
        _ = CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _);
        _ = CapabilitySecretRequirement.TryParse(secretReference, out var secret, out _);
        return new CapabilityDescriptor(
            CapabilityDescriptor.CurrentSchemaVersion,
            id!,
            CapabilityKind.Actuator,
            version!,
            new CapabilityImplementationIdentity(provider!, "read-workspace"),
            new CapabilityProvenance(CapabilityProvenanceKind.LocalSource, "file:///workspace/capabilities/read-workspace", "revision-1", null),
            new CapabilityCompatibility(range!, [CapabilityPlatform.Any]),
            "Read bounded workspace state after separate governance admits the action.",
            schema!,
            schema!,
            new CapabilityResourceLimits(30_000, 134_217_728, 1_048_576, 4),
            CapabilitySideEffectClass.ReadOnly,
            new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], [secret!]));
    }
}
