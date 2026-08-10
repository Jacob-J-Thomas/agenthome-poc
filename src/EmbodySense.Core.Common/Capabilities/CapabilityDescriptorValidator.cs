using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Validates closed schema-version-1 capability descriptors without granting authority.
/// </summary>
public static class CapabilityDescriptorValidator
{
    /// <summary>
    /// Validates all bounded descriptor fields and returns structured errors.
    /// </summary>
    /// <param name="descriptor">The descriptor to validate.</param>
    /// <returns>The complete bounded validation result.</returns>
    public static CapabilityContractValidationResult Validate(CapabilityDescriptor? descriptor)
    {
        var errors = new List<CapabilityContractError>();
        if (descriptor is null)
        {
            Add(errors, "descriptor_required", "$", "A capability descriptor is required.");
            return new CapabilityContractValidationResult(errors);
        }

        if (descriptor.SchemaVersion != CapabilityDescriptor.CurrentSchemaVersion)
        {
            Add(errors, "unsupported_schema_version", "schemaVersion", "Only experimental capability descriptor schema version 1 is supported.");
        }

        Require(descriptor.Id, "id", errors);
        if (!IsSupported(descriptor.Kind))
        {
            Add(errors, "unsupported_capability_kind", "kind", "The capability kind is absent or unsupported.");
        }

        Require(descriptor.Version, "version", errors);
        ValidateImplementation(descriptor.Implementation, errors);
        ValidateProvenance(descriptor.Provenance, errors);
        ValidateCompatibility(descriptor.Compatibility, errors);
        ValidatePurpose(descriptor.Purpose, errors);
        Require(descriptor.InputSchema, "inputSchema", errors);
        Require(descriptor.OutputSchema, "outputSchema", errors);
        ValidateResourceLimits(descriptor.ResourceLimits, errors);
        if (!IsSupported(descriptor.SideEffectClass))
        {
            Add(errors, "unsupported_side_effect_class", "sideEffectClass", "The side-effect class is absent or unsupported.");
        }

        ValidateRequirements(descriptor.Requirements, errors);
        return new CapabilityContractValidationResult(errors);
    }

    private static void ValidateImplementation(CapabilityImplementationIdentity? implementation, List<CapabilityContractError> errors)
    {
        if (implementation is null)
        {
            Add(errors, "implementation_required", "implementation", "An implementation identity is required.");
            return;
        }

        Require(implementation.ProviderId, "implementation.providerId", errors);
        if (!CapabilityIdentifierRules.IsPath(implementation.ImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters))
        {
            Add(errors, "invalid_implementation_id", "implementation.implementationId", "Implementation ids must be bounded canonical lowercase ASCII paths.");
        }
    }

    private static void ValidateProvenance(CapabilityProvenance? provenance, List<CapabilityContractError> errors)
    {
        if (provenance is null)
        {
            Add(errors, "provenance_required", "provenance", "Safe implementation provenance is required.");
            return;
        }

        if (!IsSupported(provenance.Kind))
        {
            Add(errors, "unsupported_provenance_kind", "provenance.kind", "The provenance kind is absent or unsupported.");
        }

        if (!IsSafeSourceUri(provenance.SourceUri))
        {
            Add(errors, "invalid_provenance_source", "provenance.sourceUri", "Provenance sources must be canonical absolute HTTPS, file, package, or URN values without credentials, query, or fragment.");
        }

        if (provenance.SourceRevision is not null && !IsSafeRevision(provenance.SourceRevision))
        {
            Add(errors, "invalid_source_revision", "provenance.sourceRevision", "Source revisions must be bounded printable ASCII identifiers without whitespace or credential delimiters.");
        }

        if (provenance.Kind == CapabilityProvenanceKind.RemoteArtifact && provenance.Integrity is null)
        {
            Add(errors, "integrity_digest_required", "provenance.integrity", "Remote artifacts require a content integrity digest.");
        }
    }

    private static void ValidateCompatibility(CapabilityCompatibility? compatibility, List<CapabilityContractError> errors)
    {
        if (compatibility is null)
        {
            Add(errors, "compatibility_required", "compatibility", "A compatibility declaration is required.");
            return;
        }

        Require(compatibility.HostVersionRange, "compatibility.hostVersionRange", errors);
        var platforms = compatibility.SupportedPlatforms;
        if (platforms is null || platforms.Count is < 1 or > CapabilityContractLimits.MaxPlatforms)
        {
            Add(errors, "platform_count_out_of_range", "compatibility.supportedPlatforms", $"Descriptors must declare between 1 and {CapabilityContractLimits.MaxPlatforms} platforms.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < platforms.Count; index++)
        {
            var platform = platforms[index];
            if (platform is null)
            {
                Add(errors, "platform_required", $"compatibility.supportedPlatforms[{index}]", "A supported platform cannot be null.");
            }
            else if (!seen.Add(platform.ToString()))
            {
                Add(errors, "duplicate_platform", $"compatibility.supportedPlatforms[{index}]", "Supported platforms must be unique.");
            }
        }

        if (seen.Contains(CapabilityPlatform.Any.ToString()) && seen.Count > 1)
        {
            Add(errors, "ambiguous_platforms", "compatibility.supportedPlatforms", "The any/any platform cannot be combined with specific platforms.");
        }
    }

    private static void ValidatePurpose(string? purpose, List<CapabilityContractError> errors)
    {
        if (string.IsNullOrWhiteSpace(purpose))
        {
            Add(errors, "purpose_required", "purpose", "A stable human-readable purpose is required.");
        }
        else if (!CapabilityTextRules.IsSafeNormalized(purpose, CapabilityContractLimits.MaxPurposeCharacters, allowEmpty: false))
        {
            Add(errors, "invalid_purpose", "purpose", "The purpose must be bounded NFC text without unsafe Unicode.");
        }
    }

    private static void ValidateResourceLimits(CapabilityResourceLimits? limits, List<CapabilityContractError> errors)
    {
        if (limits is null)
        {
            Add(errors, "resource_limits_required", "resourceLimits", "Bounded resource limits are required.");
            return;
        }

        ValidateRange(limits.MaxExecutionMilliseconds, 1, CapabilityContractLimits.MaxExecutionMilliseconds, "resourceLimits.maxExecutionMilliseconds", errors);
        ValidateRange(limits.MaxMemoryBytes, 1, CapabilityContractLimits.MaxMemoryBytes, "resourceLimits.maxMemoryBytes", errors);
        ValidateRange(limits.MaxOutputBytes, 1, CapabilityContractLimits.MaxOutputBytes, "resourceLimits.maxOutputBytes", errors);
        ValidateRange(limits.MaxConcurrency, 1, CapabilityContractLimits.MaxConcurrency, "resourceLimits.maxConcurrency", errors);
    }

    private static void ValidateRequirements(CapabilityAccessRequirements? requirements, List<CapabilityContractError> errors)
    {
        if (requirements is null)
        {
            Add(errors, "requirements_required", "requirements", "Data, egress, and secret requirements are required.");
            return;
        }

        ValidateUniqueValues(requirements.DataClasses, CapabilityContractLimits.MaxDataClasses, "requirements.dataClasses", item => item.Value, errors);
        ValidateUniqueStrings(requirements.EgressDestinations, CapabilityContractLimits.MaxEgressDestinations, "requirements.egressDestinations", CapabilityIdentifierRules.IsHost, errors);
        ValidateUniqueValues(requirements.Secrets, CapabilityContractLimits.MaxSecretRequirements, "requirements.secrets", item => item.Name, errors);

        if (!IsSupported(requirements.EgressMode))
        {
            Add(errors, "unsupported_egress_mode", "requirements.egressMode", "The egress mode is absent or unsupported.");
        }
        else if (requirements.EgressMode == CapabilityEgressMode.Restricted && requirements.EgressDestinations?.Count is not > 0)
        {
            Add(errors, "egress_destinations_required", "requirements.egressDestinations", "Restricted egress requires at least one canonical destination.");
        }
        else if (requirements.EgressMode != CapabilityEgressMode.Restricted && requirements.EgressDestinations?.Count is > 0)
        {
            Add(errors, "unexpected_egress_destinations", "requirements.egressDestinations", "Only restricted egress may declare destinations.");
        }
    }

    private static void ValidateUniqueValues<T>(IReadOnlyList<T>? values, int maximum, string field, Func<T, string> selectKey, List<CapabilityContractError> errors) where T : class
    {
        if (values is null || values.Count > maximum)
        {
            Add(errors, "collection_out_of_range", field, $"The collection must contain at most {maximum} entries.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is not { } value)
            {
                Add(errors, "collection_item_required", $"{field}[{index}]", "Collection entries cannot be null.");
            }
            else if (!seen.Add(selectKey(value)))
            {
                Add(errors, "duplicate_collection_item", $"{field}[{index}]", "Collection entries must be unique.");
            }
        }
    }

    private static void ValidateUniqueStrings(IReadOnlyList<string>? values, int maximum, string field, Func<string?, bool> isValid, List<CapabilityContractError> errors)
    {
        if (values is null || values.Count > maximum)
        {
            Add(errors, "collection_out_of_range", field, $"The collection must contain at most {maximum} entries.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (!isValid(value))
            {
                Add(errors, "invalid_collection_item", $"{field}[{index}]", "The collection entry is not canonical or safe.");
            }
            else if (!seen.Add(value))
            {
                Add(errors, "duplicate_collection_item", $"{field}[{index}]", "Collection entries must be unique.");
            }
        }
    }

    private static bool IsSafeSourceUri(string? value)
    {
        if (!CapabilityTextRules.IsSafeAsciiToken(value, CapabilityContractLimits.MaxSourceUriCharacters) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (uri.Scheme is not "https" and not "file" and not "pkg" and not "urn")
        {
            return false;
        }

        return string.Equals(uri.AbsoluteUri, value, StringComparison.Ordinal);
    }

    private static bool IsSafeRevision(string value)
    {
        return value.Length is >= 1 and <= CapabilityContractLimits.MaxSourceRevisionCharacters && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '/' or '@');
    }

    private static bool IsSupported<T>(T value) where T : struct, Enum
    {
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) != 0 && Enum.IsDefined(value);
    }

    private static void Require(object? value, string field, List<CapabilityContractError> errors)
    {
        if (value is null)
        {
            Add(errors, "contract_value_required", field, "The contract value is required.");
        }
    }

    private static void ValidateRange(long value, long minimum, long maximum, string field, List<CapabilityContractError> errors)
    {
        if (value < minimum || value > maximum)
        {
            Add(errors, "resource_limit_out_of_range", field, $"The resource limit must be between {minimum} and {maximum}.");
        }
    }

    private static void Add(List<CapabilityContractError> errors, string code, string field, string message)
    {
        errors.Add(new CapabilityContractError(code, field, message));
    }
}
