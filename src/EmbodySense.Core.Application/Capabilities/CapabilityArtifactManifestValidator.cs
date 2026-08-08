using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Validates the bounded schema-version-1 artifact manifest without making trust decisions.</summary>
public static class CapabilityArtifactManifestValidator
{
    /// <summary>Gets the maximum fixed argument count.</summary>
    public const int MaximumArguments = 32;
    /// <summary>Gets the maximum artifact entry-point length.</summary>
    public const int MaximumEntryPointCharacters = 512;
    /// <summary>Gets the maximum individual fixed argument length.</summary>
    public const int MaximumArgumentCharacters = 2_048;
    /// <summary>Gets the maximum operation identity length.</summary>
    public const int MaximumOperationIdCharacters = 128;
    /// <summary>Gets the maximum artifact payload size.</summary>
    public const int MaximumArtifactBytes = 64 * 1024 * 1024;

    /// <summary>Validates one artifact manifest and returns every bounded structural rejection.</summary>
    public static CapabilityContractValidationResult Validate(CapabilityArtifactManifest? manifest)
    {
        var errors = new List<CapabilityContractError>();
        if (manifest is null)
        {
            errors.Add(Error("artifact_manifest_required", "$", "An artifact manifest is required."));
            return new CapabilityContractValidationResult(errors);
        }

        if (manifest.SchemaVersion != CapabilityArtifactManifest.CurrentSchemaVersion)
        {
            errors.Add(Error("unsupported_schema_version", "schemaVersion", "Only experimental artifact schema version 1 is supported."));
        }

        foreach (var error in CapabilityDescriptorValidator.Validate(manifest.Descriptor).Errors)
        {
            errors.Add(new CapabilityContractError(error.Code, "descriptor." + error.Field, error.Message));
        }

        ValidateSource(manifest, errors);
        if (manifest.Checksum is null)
        {
            errors.Add(Error("artifact_checksum_required", "checksum", "Artifact intake requires an exact SHA-256 checksum."));
        }
        else if (manifest.Descriptor?.Provenance?.Integrity is { } descriptorDigest && !descriptorDigest.FixedTimeEquals(manifest.Checksum))
        {
            errors.Add(Error("artifact_checksum_conflict", "checksum", "Artifact and descriptor integrity evidence must identify the same bytes."));
        }

        ValidateSignature(manifest.Signature, errors);
        if (manifest.Platform is null)
        {
            errors.Add(Error("artifact_platform_required", "platform", "An exact artifact platform is required."));
        }
        else if (manifest.Platform.Equals(CapabilityPlatform.Any))
        {
            errors.Add(Error("artifact_platform_must_be_exact", "platform", "Executable artifacts require one exact operating-system/architecture tuple."));
        }
        else if (manifest.Descriptor?.Compatibility?.SupportedPlatforms is { } platforms && !platforms.Any(platform => platform.Equals(CapabilityPlatform.Any) || platform.Equals(manifest.Platform)))
        {
            errors.Add(Error("artifact_platform_conflict", "platform", "The artifact platform is outside descriptor compatibility."));
        }

        if (!IsContainedRelativePath(manifest.EntryPoint))
        {
            errors.Add(Error("invalid_artifact_entry_point", "entryPoint", "The entry point must be one bounded contained relative path."));
        }

        if (manifest.Arguments is null || manifest.Arguments.Count > MaximumArguments)
        {
            errors.Add(Error("artifact_arguments_out_of_range", "arguments", $"Artifacts may declare at most {MaximumArguments} fixed arguments."));
        }
        else
        {
            for (var index = 0; index < manifest.Arguments.Count; index++)
            {
                if (!IsSafeText(manifest.Arguments[index], MaximumArgumentCharacters, allowEmpty: true))
                {
                    errors.Add(Error("invalid_artifact_argument", $"arguments[{index}]", "Fixed arguments must be bounded safe text without control characters."));
                }
            }
        }

        return new CapabilityContractValidationResult(errors);
    }

    /// <summary>Checks a bounded canonical operation identity.</summary>
    public static bool IsOperationId(string? value) => IsSafeAscii(value, MaximumOperationIdCharacters);

    private static void ValidateSource(CapabilityArtifactManifest manifest, List<CapabilityContractError> errors)
    {
        var source = manifest.Source;
        if (source is null || source.Kind == CapabilityArtifactSourceKind.Unknown || !Enum.IsDefined(source.Kind))
        {
            errors.Add(Error("artifact_source_required", "source", "A supported local or remote artifact source is required."));
            return;
        }

        var expectedScheme = source.Kind == CapabilityArtifactSourceKind.Local ? "file" : "https";
        if (!Uri.TryCreate(source.Uri, UriKind.Absolute, out var uri) || uri.Scheme != expectedScheme || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.Equals(uri.AbsoluteUri, source.Uri, StringComparison.Ordinal))
        {
            errors.Add(Error("invalid_artifact_source", "source.uri", $"{source.Kind} artifact sources require one canonical {expectedScheme} URI without credentials, query, or fragment."));
        }

        if (!IsSafeAscii(source.Revision, CapabilityContractLimits.MaxSourceRevisionCharacters))
        {
            errors.Add(Error("invalid_artifact_revision", "source.revision", "The exact source revision must be bounded printable ASCII."));
        }

        if (source.UpdatePolicy == CapabilityArtifactUpdatePolicy.Unknown || !Enum.IsDefined(source.UpdatePolicy))
        {
            errors.Add(Error("invalid_artifact_update_policy", "source.updatePolicy", "A supported explicit update policy is required."));
        }

        var provenance = manifest.Descriptor?.Provenance;
        var expectedProvenance = source.Kind == CapabilityArtifactSourceKind.Local ? CapabilityProvenanceKind.LocalSource : CapabilityProvenanceKind.RemoteArtifact;
        if (provenance is not null && (provenance.Kind != expectedProvenance || !string.Equals(provenance.SourceUri, source.Uri, StringComparison.Ordinal) || !string.Equals(provenance.SourceRevision, source.Revision, StringComparison.Ordinal)))
        {
            errors.Add(Error("artifact_provenance_conflict", "source", "Artifact source evidence must exactly match descriptor provenance."));
        }
    }

    private static void ValidateSignature(CapabilityArtifactSignatureEvidence? signature, List<CapabilityContractError> errors)
    {
        if (signature is null)
        {
            return;
        }

        if (signature.Algorithm != "ecdsa-p256-sha256" || !IsSafeAscii(signature.KeyId, 128) || signature.Signature.Length is < 1 or > CapabilityContractLimits.MaxArtifactSignatureCharacters || !Convert.TryFromBase64String(signature.Signature, new byte[CapabilityContractLimits.MaxArtifactSignatureCharacters], out _))
        {
            errors.Add(Error("invalid_artifact_signature", "signature", "Signatures must use ecdsa-p256-sha256, a bounded key id, and bounded base64 evidence."));
        }
    }

    private static bool IsContainedRelativePath(string? value)
    {
        if (!IsSafeText(value, MaximumEntryPointCharacters, allowEmpty: false) || Path.IsPathRooted(value) || value!.Contains(':'))
        {
            return false;
        }

        var components = value.Replace('\\', '/').Split('/');
        return components.All(component => component.Length > 0 && component is not "." and not "..");
    }

    private static bool IsSafeAscii(string? value, int maximum) => value is not null && value.Length is > 0 && value.Length <= maximum && value.All(character => character is >= (char)0x21 and <= (char)0x7e);

    private static bool IsSafeText(string? value, int maximum, bool allowEmpty) => value is not null && value.Length <= maximum && (allowEmpty || value.Length > 0) && value.All(character => character >= (char)0x20 && character != (char)0x7f);

    private static CapabilityContractError Error(string code, string field, string message) => new(code, field, message);
}
