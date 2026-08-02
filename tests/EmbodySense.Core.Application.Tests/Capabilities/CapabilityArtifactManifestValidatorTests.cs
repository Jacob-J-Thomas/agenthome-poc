using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

public sealed class CapabilityArtifactManifestValidatorTests
{
    [Fact]
    public void Valid_schema_one_manifest_preserves_bounded_source_and_execution_evidence()
    {
        var manifest = CapabilityArtifactTestData.Manifest();

        var result = CapabilityArtifactManifestValidator.Validate(manifest);

        Assert.True(result.IsValid, string.Join(", ", result.Errors.Select(error => error.Code)));
        Assert.Equal(CapabilityArtifactUpdatePolicy.Pinned, manifest.Source.UpdatePolicy);
        Assert.Equal("rev-1", manifest.Source.Revision);
        Assert.Equal("echo.exe", manifest.EntryPoint);
    }

    [Fact]
    public void Forged_provenance_path_escape_platform_ambiguity_and_unsupported_schema_fail_closed()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var forged = manifest with
        {
            SchemaVersion = 2,
            Source = manifest.Source with { Uri = "file:///different/echo.exe", UpdatePolicy = CapabilityArtifactUpdatePolicy.Unknown },
            Platform = EmbodySense.Core.Common.Capabilities.CapabilityPlatform.Any,
            EntryPoint = "../echo.exe"
        };

        var codes = CapabilityArtifactManifestValidator.Validate(forged).Errors.Select(error => error.Code).ToArray();

        Assert.Contains("unsupported_schema_version", codes);
        Assert.Contains("invalid_artifact_update_policy", codes);
        Assert.Contains("artifact_provenance_conflict", codes);
        Assert.Contains("artifact_platform_must_be_exact", codes);
        Assert.Contains("invalid_artifact_entry_point", codes);
    }

    [Fact]
    public void Malformed_signature_and_checksum_conflict_are_rejected()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var changedDigest = EmbodySense.Core.Common.Capabilities.CapabilityIntegrityDigest.Compute("other"u8);
        var malformed = manifest with { Checksum = changedDigest, Signature = new CapabilityArtifactSignatureEvidence("artifact-says-trusted", "key", "not-base64") };

        var codes = CapabilityArtifactManifestValidator.Validate(malformed).Errors.Select(error => error.Code).ToArray();

        Assert.Contains("artifact_checksum_conflict", codes);
        Assert.Contains("invalid_artifact_signature", codes);
    }

    [Fact]
    public void Missing_manifest_descriptor_checksum_platform_source_and_arguments_are_all_rejected()
    {
        Assert.Contains(CapabilityArtifactManifestValidator.Validate(null).Errors, error => error.Code == "artifact_manifest_required");
        var manifest = CapabilityArtifactTestData.Manifest();
        var missing = new CapabilityArtifactManifest(manifest.SchemaVersion, null!, null!, null!, manifest.Signature, null!, manifest.EntryPoint, null!);

        var codes = CapabilityArtifactManifestValidator.Validate(missing).Errors.Select(error => error.Code).ToArray();

        Assert.Contains("descriptor_required", codes);
        Assert.Contains("artifact_checksum_required", codes);
        Assert.Contains("artifact_platform_required", codes);
        Assert.Contains("artifact_source_required", codes);
        Assert.Contains("artifact_arguments_out_of_range", codes);
    }

    [Fact]
    public void Source_platform_argument_count_and_argument_text_bounds_are_enforced()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var incompatible = CapabilityArtifactTestData.Platform("linux/x64");
        var malformed = new CapabilityArtifactManifest(manifest.SchemaVersion, manifest.Descriptor, manifest.Source with { Uri = "file:///artifact?credential=value", Revision = "contains space" }, manifest.Checksum, manifest.Signature, incompatible, manifest.EntryPoint, Enumerable.Repeat("argument", CapabilityArtifactManifestValidator.MaximumArguments + 1).ToArray());

        var codes = CapabilityArtifactManifestValidator.Validate(malformed).Errors.Select(error => error.Code).ToArray();
        Assert.Contains("invalid_artifact_source", codes);
        Assert.Contains("invalid_artifact_revision", codes);
        Assert.Contains("artifact_platform_conflict", codes);
        Assert.Contains("artifact_arguments_out_of_range", codes);

        var unsafeArgument = new CapabilityArtifactManifest(manifest.SchemaVersion, manifest.Descriptor, manifest.Source, manifest.Checksum, manifest.Signature, manifest.Platform, manifest.EntryPoint, ["unsafe\u0001argument"]);
        Assert.Contains(CapabilityArtifactManifestValidator.Validate(unsafeArgument).Errors, error => error.Code == "invalid_artifact_argument");
    }

    [Fact]
    public void Valid_bounded_signature_and_nested_entry_point_are_accepted()
    {
        var manifest = CapabilityArtifactTestData.Manifest() with
        {
            Signature = new CapabilityArtifactSignatureEvidence("ecdsa-p256-sha256", "trusted-key", Convert.ToBase64String([1, 2, 3])),
            EntryPoint = "bin/echo.exe"
        };

        Assert.True(CapabilityArtifactManifestValidator.Validate(manifest).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void Unsafe_operation_id_is_rejected(string operationId) => Assert.False(CapabilityArtifactManifestValidator.IsOperationId(operationId));
}
