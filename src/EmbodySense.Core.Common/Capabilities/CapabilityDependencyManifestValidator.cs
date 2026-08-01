using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>Validates closed bounded schema-version-1 capability dependency manifests.</summary>
public static class CapabilityDependencyManifestValidator
{
    /// <summary>Validates all manifest fields without granting any authority.</summary>
    public static CapabilityContractValidationResult Validate(CapabilityDependencyManifest? manifest)
    {
        var errors = new List<CapabilityContractError>();
        if (manifest is null)
        {
            return new CapabilityContractValidationResult([new CapabilityContractError("dependency_manifest_required", "$", "A capability dependency manifest is required.")]);
        }

        if (manifest.SchemaVersion != CapabilityDependencyManifest.CurrentSchemaVersion)
        {
            Add(errors, "unsupported_schema_version", "schemaVersion", "Only capability dependency manifest schema version 1 is supported.");
        }

        if (manifest.Kind is not CapabilityDependencyManifestKind.Skill and not CapabilityDependencyManifestKind.LoopPackage)
        {
            Add(errors, "unsupported_dependency_manifest_kind", "kind", "The dependency manifest kind is absent or unsupported.");
        }

        if (manifest.SubjectId is null)
        {
            Add(errors, "dependency_subject_required", "subjectId", "A canonical subject capability id is required.");
        }

        ValidateDependencies(manifest.Required, "required", errors, new HashSet<string>(StringComparer.Ordinal));
        var requiredIds = manifest.Required?.Where(item => item?.CapabilityId is not null).Select(item => item.CapabilityId.Value).ToHashSet(StringComparer.Ordinal) ?? [];
        ValidateDependencies(manifest.Optional, "optional", errors, requiredIds);
        ValidateArtifact(manifest.Artifact, errors);
        return new CapabilityContractValidationResult(errors);
    }

    private static void ValidateDependencies(IReadOnlyList<CapabilityDependency>? dependencies, string field, List<CapabilityContractError> errors, HashSet<string> ids)
    {
        if (dependencies is null || dependencies.Count > CapabilityContractLimits.MaxDependencyManifestDependencies)
        {
            Add(errors, "dependency_collection_out_of_range", field, $"The dependency collection must contain at most {CapabilityContractLimits.MaxDependencyManifestDependencies} entries.");
            return;
        }

        for (var index = 0; index < dependencies.Count; index++)
        {
            var dependency = dependencies[index];
            if (dependency?.CapabilityId is null || dependency.CompatibleVersionRange is null)
            {
                Add(errors, "dependency_required", $"{field}[{index}]", "Every dependency requires a canonical capability id and compatible-version range.");
            }
            else if (!ids.Add(dependency.CapabilityId.Value))
            {
                Add(errors, "duplicate_dependency", $"{field}[{index}]", "A capability can appear only once across required and optional dependencies.");
            }
        }
    }

    private static void ValidateArtifact(CapabilityDependencyArtifactMetadata? artifact, List<CapabilityContractError> errors)
    {
        if (artifact is null)
        {
            Add(errors, "artifact_metadata_required", "artifact", "Artifact metadata is required even when no checksum or signature is available.");
            return;
        }

        if (artifact.Signature is not null && !CapabilityTextRules.IsSafeAsciiToken(artifact.Signature, CapabilityContractLimits.MaxArtifactSignatureCharacters))
        {
            Add(errors, "invalid_artifact_signature", "artifact.signature", "Signature evidence must be bounded printable ASCII without whitespace.");
        }
    }

    private static void Add(List<CapabilityContractError> errors, string code, string field, string message)
    {
        if (errors.Count < 64)
        {
            errors.Add(new CapabilityContractError(code, field, message));
        }
    }
}
