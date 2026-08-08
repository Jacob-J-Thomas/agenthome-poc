using System.Globalization;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Tests;

public sealed class CapabilityDependencyManifestTests
{
    [Fact]
    public void Canonical_json_and_hash_are_culture_and_enumeration_order_independent()
    {
        var first = Manifest([Dependency("org.example/z", "[1.0.0,2.0.0)"), Dependency("org.example/a", "*")]);
        var second = Manifest([Dependency("org.example/a", "*"), Dependency("org.example/z", "[1.0.0,2.0.0)")]);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.True(CapabilityDependencyManifestJson.TrySerialize(first, out var firstJson, out var firstValidation));
            Assert.True(CapabilityDependencyManifestJson.TrySerialize(second, out var secondJson, out var secondValidation));
            Assert.True(firstValidation.IsValid);
            Assert.True(secondValidation.IsValid);
            Assert.Equal(firstJson, secondJson);
            Assert.True(CapabilityDependencyManifestHash.TryCompute(first, out var firstHash, out _));
            Assert.True(CapabilityDependencyManifestHash.TryCompute(second, out var secondHash, out _));
            Assert.Equal(firstHash, secondHash);
            Assert.True(CapabilityDependencyManifestJson.TryDeserialize(firstJson, out var readback, out var readbackValidation));
            Assert.True(readbackValidation.IsValid);
            Assert.True(CapabilityDependencyManifestJson.TrySerialize(readback, out var readbackJson, out _));
            Assert.Equal(firstJson, readbackJson);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData("{\"artifact\":{\"checksum\":null,\"signature\":null},\"kind\":\"skill\",\"optional\":[],\"required\":[],\"schemaVersion\":1,\"subjectId\":\"org.example/skill\",\"trust\":true}")]
    [InlineData("{\"artifact\":{\"checksum\":null,\"signature\":null},\"kind\":\"skill\",\"optional\":[],\"required\":[{\"capabilityId\":\"org.example/a\",\"compatibleVersionRange\":\"*\"}],\"schemaVersion\":1,\"subjectId\":\"org.example/skill\",\"required\":[]}")]
    public void Hostile_or_authority_bearing_json_fails_closed(string json)
    {
        Assert.False(CapabilityDependencyManifestJson.TryDeserialize(json, out _, out var validation));
        Assert.False(validation.IsValid);
    }

    [Fact]
    public void Required_and_optional_dependencies_cannot_overlap()
    {
        var dependency = Dependency("org.example/a", "*");
        var manifest = Manifest([dependency]) with { Optional = [dependency] };

        var validation = CapabilityDependencyManifestValidator.Validate(manifest);

        Assert.Contains(validation.Errors, error => error.Code == "duplicate_dependency");
    }

    private static CapabilityDependencyManifest Manifest(IReadOnlyList<CapabilityDependency> required) => new(1, CapabilityDependencyManifestKind.Skill, Id("org.example/skill"), required, [], new CapabilityDependencyArtifactMetadata(null, null));

    private static CapabilityDependency Dependency(string id, string range) => new(Id(id), Range(range));

    private static CapabilityId Id(string value)
    {
        Assert.True(CapabilityId.TryParse(value, out var id, out _));
        return id!;
    }

    private static CapabilityVersionRange Range(string value)
    {
        Assert.True(CapabilityVersionRange.TryParse(value, out var range, out _));
        return range!;
    }
}
