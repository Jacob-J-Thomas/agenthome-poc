using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Tests;

internal static class CapabilityContractTestData
{
    internal static CapabilityDescriptor ValidDescriptor(bool reverseCollections = false, bool reverseSchemas = false)
    {
        var platforms = new[] { Platform("windows/x64"), Platform("linux/x64") };
        var dataClasses = new[] { DataClass("workspace-content"), DataClass("user-content") };
        var destinations = new[] { "api.example.com", "models.example.com" };
        var secrets = new[] { Secret("provider-token"), Secret("audit-key") };
        if (reverseCollections)
        {
            Array.Reverse(platforms);
            Array.Reverse(dataClasses);
            Array.Reverse(destinations);
            Array.Reverse(secrets);
        }

        var firstSchema = $"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\",\"properties\":{{\"z\":{{\"type\":\"string\"}},\"a\":{{\"type\":\"integer\"}}}}}}";
        var secondSchema = $"{{\"type\":\"object\",\"properties\":{{\"a\":{{\"type\":\"integer\"}},\"z\":{{\"type\":\"string\"}}}},\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\"}}";
        var schema = Schema(reverseSchemas ? secondSchema : firstSchema);
        return new CapabilityDescriptor(
            CapabilityDescriptor.CurrentSchemaVersion,
            Id("org.embodysense/workspace/read-file"),
            CapabilityKind.Actuator,
            Version("1.2.3-beta.2+build.7"),
            new CapabilityImplementationIdentity(Provider("org.embodysense"), "workspace/read-file"),
            new CapabilityProvenance(CapabilityProvenanceKind.RemoteArtifact, "https://artifacts.example.com/read-file", "commit-123", Digest(new string('a', 64))),
            new CapabilityCompatibility(Range("[1.0.0,2.0.0)"), platforms),
            "Read one bounded workspace file after governance permits the operation.",
            schema,
            Schema($"{{\"type\":\"object\",\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\"}}"),
            new CapabilityResourceLimits(30_000, 134_217_728, 1_048_576, 4),
            CapabilitySideEffectClass.ReadOnly,
            new CapabilityAccessRequirements(dataClasses, CapabilityEgressMode.Restricted, destinations, secrets));
    }

    internal static CapabilityId Id(string value)
    {
        Assert.True(CapabilityId.TryParse(value, out var parsed, out var error), error?.Message);
        return parsed!;
    }

    internal static CapabilityProviderId Provider(string value)
    {
        Assert.True(CapabilityProviderId.TryParse(value, out var parsed, out var error), error?.Message);
        return parsed!;
    }

    internal static CapabilityVersion Version(string value)
    {
        Assert.True(CapabilityVersion.TryParse(value, out var parsed, out var error), error?.Message);
        return parsed!;
    }

    internal static CapabilityVersionRange Range(string value)
    {
        Assert.True(CapabilityVersionRange.TryParse(value, out var parsed, out var error), error?.Message);
        return parsed!;
    }

    internal static CapabilityPlatform Platform(string value)
    {
        Assert.True(CapabilityPlatform.TryParse(value, out var parsed, out var error), error?.Message);
        return parsed!;
    }

    internal static CapabilityDataClass DataClass(string value)
    {
        Assert.True(CapabilityDataClass.TryParse(value, out var parsed, out var error), error?.Message);
        return parsed!;
    }

    internal static CapabilitySecretRequirement Secret(string value)
    {
        Assert.True(CapabilitySecretRequirement.TryParse(value, out var parsed, out var error), error?.Message);
        return parsed!;
    }

    internal static CapabilityIntegrityDigest Digest(string hex)
    {
        Assert.True(CapabilityIntegrityDigest.TryParse($"sha256:{hex}", out var parsed, out var error), error?.Message);
        return parsed!;
    }

    internal static CapabilityJsonSchema Schema(string json)
    {
        Assert.True(CapabilityJsonSchema.TryCreate(json, out var parsed, out var error), error?.Message);
        return parsed!;
    }
}
