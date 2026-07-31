using System.Globalization;
using System.Text.Json;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.Tests;

public sealed class CapabilityDescriptorTests
{
    [Fact]
    public void Valid_descriptor_round_trips_and_hashes_independently_of_culture_and_set_order()
    {
        var descriptor = CapabilityContractTestData.ValidDescriptor();
        var reordered = CapabilityContractTestData.ValidDescriptor(reverseCollections: true, reverseSchemas: true);
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.True(CapabilityDescriptorJson.TrySerialize(descriptor, out var json, out var serialization));
            Assert.True(serialization.IsValid);
            Assert.NotNull(json);
            Assert.True(CapabilityDescriptorJson.TryDeserialize(json, out var roundTrip, out var deserialization));
            Assert.True(deserialization.IsValid);
            Assert.NotNull(roundTrip);
            Assert.True(CapabilityDescriptorJson.TrySerialize(roundTrip, out var roundTripJson, out _));
            Assert.Equal(json, roundTripJson);

            Assert.True(CapabilityDescriptorHash.TryCompute(descriptor, out var firstHash, out var firstValidation));
            Assert.True(CapabilityDescriptorHash.TryCompute(reordered, out var reorderedHash, out var reorderedValidation));
            Assert.True(firstValidation.IsValid);
            Assert.True(reorderedValidation.IsValid);
            Assert.Equal(firstHash, reorderedHash);
            Assert.Equal(firstHash?.Value, firstHash?.ToString());
            Assert.True(firstHash?.Equals((object)reorderedHash!) == true);
            Assert.False(firstHash?.Equals((object)firstHash.Value) == true);
            Assert.Equal(firstHash?.GetHashCode(), reorderedHash?.GetHashCode());

            Assert.True(CapabilityDescriptorHash.TryParse(firstHash?.Value, out var parsedHash, out _));
            Assert.Equal(firstHash, parsedHash);
            Assert.False(CapabilityDescriptorHash.TryParse("sha256:" + new string('A', 64), out _, out _));
            Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var identityValidation));
            Assert.True(identityValidation.IsValid);
            Assert.Equal(descriptor.Id, identity?.Id);
            Assert.Equal(descriptor.Version, identity?.Version);
            Assert.Equal(firstHash, identity?.Hash);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Descriptor_hash_preserves_exact_build_identity_while_range_build_aliases_fail_closed()
    {
        var descriptor = CapabilityContractTestData.ValidDescriptor();
        var differentBuild = descriptor with { Version = CapabilityContractTestData.Version("1.2.3-beta.2+build.8") };

        Assert.True(CapabilityDescriptorHash.TryCompute(descriptor, out var firstHash, out _));
        Assert.True(CapabilityDescriptorHash.TryCompute(differentBuild, out var differentBuildHash, out _));
        Assert.NotEqual(firstHash, differentBuildHash);

        Assert.True(CapabilityDescriptorJson.TrySerialize(descriptor, out var json, out _));
        var aliasedRange = json!.Replace("[1.0.0,2.0.0)", "[1.0.0+range.1,2.0.0)", StringComparison.Ordinal);
        Assert.NotEqual(json, aliasedRange);
        AssertRejected(aliasedRange, "invalid_capability_version_range");
    }

    [Fact]
    public void Collection_models_snapshot_inputs_before_concurrent_caller_mutation()
    {
        var platforms = new List<CapabilityPlatform> { CapabilityContractTestData.Platform("windows/x64"), CapabilityContractTestData.Platform("linux/x64") };
        var dataClasses = new List<CapabilityDataClass> { CapabilityContractTestData.DataClass("workspace-content"), CapabilityContractTestData.DataClass("user-content") };
        var destinations = new List<string> { "api.example.com", "models.example.com" };
        var secrets = new List<CapabilitySecretRequirement> { CapabilityContractTestData.Secret("provider-token"), CapabilityContractTestData.Secret("audit-key") };
        var compatibility = new CapabilityCompatibility(CapabilityContractTestData.Range("[1.0.0,2.0.0)"), platforms);
        var requirements = new CapabilityAccessRequirements(dataClasses, CapabilityEgressMode.Restricted, destinations, secrets);
        var descriptor = CapabilityContractTestData.ValidDescriptor() with { Compatibility = compatibility, Requirements = requirements };

        Assert.True(CapabilityDescriptorJson.TrySerialize(descriptor, out var expectedJson, out _));
        Assert.True(CapabilityDescriptorHash.TryCompute(descriptor, out var expectedHash, out _));
        Assert.Throws<NotSupportedException>(() => ((IList<CapabilityPlatform>)compatibility.SupportedPlatforms).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)requirements.EgressDestinations).Clear());

        Parallel.Invoke(
            () =>
            {
                for (var index = 0; index < 500; index++)
                {
                    platforms.Clear();
                    platforms.Add(CapabilityPlatform.Any);
                    dataClasses.Clear();
                    destinations.Clear();
                    secrets.Clear();
                }
            },
            () =>
            {
                for (var index = 0; index < 100; index++)
                {
                    Assert.True(CapabilityDescriptorJson.TrySerialize(descriptor, out var currentJson, out _));
                    Assert.True(CapabilityDescriptorHash.TryCompute(descriptor, out var currentHash, out _));
                    Assert.Equal(expectedJson, currentJson);
                    Assert.Equal(expectedHash, currentHash);
                }
            });

        Assert.Equal(2, compatibility.SupportedPlatforms.Count);
        Assert.Equal(2, requirements.DataClasses.Count);
        Assert.Equal(2, requirements.EgressDestinations.Count);
        Assert.Equal(2, requirements.Secrets.Count);
    }

    [Fact]
    public void Descriptor_shape_cannot_self_grant_lifecycle_trust_assignment_or_authority()
    {
        var forbidden = new[] { "Trust", "Trusted", "Authorized", "Authority", "Enabled", "Installed", "Assigned", "Granted", "Approved", "SecretValue", "Configuration", "Metadata" };
        Assert.True(CapabilityDescriptorJson.TrySerialize(CapabilityContractTestData.ValidDescriptor(), out var json, out _));
        using var descriptorDocument = JsonDocument.Parse(json!);
        var descriptorPropertyNames = descriptorDocument.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.DoesNotContain(descriptorPropertyNames, property => forbidden.Any(value => property.Contains(value, StringComparison.OrdinalIgnoreCase)));

        var identity = Assert.IsType<CapabilityDescriptorIdentity>(CreateIdentity());
        var lifecycle = new CapabilityLifecycleSnapshot(
            CapabilityLifecycleSnapshot.CurrentSchemaVersion,
            identity,
            CapabilityDeclarationState.Declared,
            CapabilityInstallationState.Installed,
            CapabilityEnablementState.Enabled,
            CapabilityHealthState.Healthy,
            CapabilityRetirementState.Active,
            CapabilityTrustState.Verified);
        using var lifecycleDocument = JsonDocument.Parse(JsonSerializer.Serialize(lifecycle));
        var lifecyclePropertyNames = lifecycleDocument.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.DoesNotContain(lifecyclePropertyNames, property => property.Contains("Author", StringComparison.OrdinalIgnoreCase) || property.Contains("Assign", StringComparison.OrdinalIgnoreCase));

        foreach (var field in new[] { "trusted", "authorized", "enabled", "assigned", "secretValue", "privateConfiguration", "metadata" })
        {
            var forged = json!.Insert(json.Length - 1, $",\"{field}\":true");
            Assert.False(CapabilityDescriptorJson.TryDeserialize(forged, out var descriptor, out var validation));
            Assert.Null(descriptor);
            Assert.Contains(validation.Errors, error => error.Code == "unknown_descriptor_property" && error.Field.EndsWith(field, StringComparison.Ordinal));
        }
    }

    private static CapabilityDescriptorIdentity? CreateIdentity()
    {
        Assert.True(CapabilityDescriptorIdentity.TryCreate(CapabilityContractTestData.ValidDescriptor(), out var identity, out var validation));
        Assert.True(validation.IsValid);
        return identity;
    }

    [Fact]
    public void Validator_reports_identity_provenance_compatibility_and_text_failures()
    {
        var valid = CapabilityContractTestData.ValidDescriptor();
        var values = new (CapabilityDescriptor? Descriptor, string Code)[]
        {
            (null, "descriptor_required"),
            (valid with { SchemaVersion = 2 }, "unsupported_schema_version"),
            (valid with { Id = null! }, "contract_value_required"),
            (valid with { Kind = CapabilityKind.Unknown }, "unsupported_capability_kind"),
            (valid with { Kind = (CapabilityKind)999 }, "unsupported_capability_kind"),
            (valid with { Version = null! }, "contract_value_required"),
            (valid with { Implementation = null! }, "implementation_required"),
            (valid with { Implementation = valid.Implementation with { ProviderId = null! } }, "contract_value_required"),
            (valid with { Implementation = valid.Implementation with { ImplementationId = "Bad Id" } }, "invalid_implementation_id"),
            (valid with { Provenance = null! }, "provenance_required"),
            (valid with { Provenance = valid.Provenance with { Kind = CapabilityProvenanceKind.Unknown } }, "unsupported_provenance_kind"),
            (valid with { Provenance = valid.Provenance with { SourceUri = "https://user:secret@example.com/artifact" } }, "invalid_provenance_source"),
            (valid with { Provenance = valid.Provenance with { SourceUri = "https://example.com/artifact?token=secret" } }, "invalid_provenance_source"),
            (valid with { Provenance = valid.Provenance with { SourceRevision = "secret=value" } }, "invalid_source_revision"),
            (valid with { Provenance = valid.Provenance with { Integrity = null } }, "integrity_digest_required"),
            (valid with { Compatibility = null! }, "compatibility_required"),
            (valid with { Compatibility = valid.Compatibility with { HostVersionRange = null! } }, "contract_value_required"),
            (valid with { Compatibility = WithPlatforms(valid.Compatibility, []) }, "platform_count_out_of_range"),
            (valid with { Compatibility = WithPlatforms(valid.Compatibility, [null!]) }, "platform_required"),
            (valid with { Compatibility = WithPlatforms(valid.Compatibility, [CapabilityPlatform.Any, CapabilityContractTestData.Platform("windows/x64")]) }, "ambiguous_platforms"),
            (valid with { Compatibility = WithPlatforms(valid.Compatibility, [CapabilityPlatform.Any, CapabilityPlatform.Any]) }, "duplicate_platform"),
            (valid with { Purpose = " " }, "purpose_required"),
            (valid with { Purpose = new string('p', CapabilityContractLimits.MaxPurposeCharacters + 1) }, "invalid_purpose"),
            (valid with { Purpose = "Cafe\u0301" }, "invalid_purpose"),
            (valid with { Purpose = "unsafe\u202e" }, "invalid_purpose"),
            (valid with { InputSchema = null! }, "contract_value_required"),
            (valid with { OutputSchema = null! }, "contract_value_required"),
            (valid with { SideEffectClass = CapabilitySideEffectClass.Unknown }, "unsupported_side_effect_class")
        };

        foreach (var (descriptor, code) in values)
        {
            var validation = CapabilityDescriptorValidator.Validate(descriptor);
            Assert.False(validation.IsValid);
            Assert.Contains(validation.Errors, error => error.Code == code);
            Assert.False(CapabilityDescriptorJson.TrySerialize(descriptor, out var json, out _));
            Assert.Null(json);
            Assert.False(CapabilityDescriptorHash.TryCompute(descriptor, out var hash, out _));
            Assert.Null(hash);
            Assert.False(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
            Assert.Null(identity);
        }
    }

    [Fact]
    public void Validator_enforces_resource_and_requirement_bounds()
    {
        var valid = CapabilityContractTestData.ValidDescriptor();
        var data = valid.Requirements.DataClasses[0];
        var secret = valid.Requirements.Secrets[0];
        var values = new (CapabilityDescriptor Descriptor, string Code)[]
        {
            (valid with { ResourceLimits = null! }, "resource_limits_required"),
            (valid with { ResourceLimits = valid.ResourceLimits with { MaxExecutionMilliseconds = 0 } }, "resource_limit_out_of_range"),
            (valid with { ResourceLimits = valid.ResourceLimits with { MaxMemoryBytes = CapabilityContractLimits.MaxMemoryBytes + 1 } }, "resource_limit_out_of_range"),
            (valid with { ResourceLimits = valid.ResourceLimits with { MaxOutputBytes = 0 } }, "resource_limit_out_of_range"),
            (valid with { ResourceLimits = valid.ResourceLimits with { MaxConcurrency = CapabilityContractLimits.MaxConcurrency + 1 } }, "resource_limit_out_of_range"),
            (valid with { Requirements = null! }, "requirements_required"),
            (valid with { Requirements = WithDataClasses(valid.Requirements, null!) }, "collection_out_of_range"),
            (valid with { Requirements = WithDataClasses(valid.Requirements, [null!]) }, "collection_item_required"),
            (valid with { Requirements = WithDataClasses(valid.Requirements, [data, data]) }, "duplicate_collection_item"),
            (valid with { Requirements = valid.Requirements with { EgressMode = CapabilityEgressMode.Unknown } }, "unsupported_egress_mode"),
            (valid with { Requirements = WithEgress(valid.Requirements, CapabilityEgressMode.Restricted, []) }, "egress_destinations_required"),
            (valid with { Requirements = valid.Requirements with { EgressMode = CapabilityEgressMode.None } }, "unexpected_egress_destinations"),
            (valid with { Requirements = WithEgressDestinations(valid.Requirements, ["https://example.com"]) }, "invalid_collection_item"),
            (valid with { Requirements = WithEgressDestinations(valid.Requirements, ["api.example.com", "api.example.com"]) }, "duplicate_collection_item"),
            (valid with { Requirements = WithSecrets(valid.Requirements, null!) }, "collection_out_of_range"),
            (valid with { Requirements = WithSecrets(valid.Requirements, [null!]) }, "collection_item_required"),
            (valid with { Requirements = WithSecrets(valid.Requirements, [secret, secret]) }, "duplicate_collection_item")
        };

        foreach (var (descriptor, code) in values)
        {
            var validation = CapabilityDescriptorValidator.Validate(descriptor);
            Assert.False(validation.IsValid);
            Assert.Contains(validation.Errors, error => error.Code == code);
        }
    }

    [Fact]
    public void Closed_json_reader_rejects_malformed_duplicate_missing_and_wrongly_typed_shapes()
    {
        Assert.True(CapabilityDescriptorJson.TrySerialize(CapabilityContractTestData.ValidDescriptor(), out var json, out _));
        var malformed = "{";
        var duplicate = json!.Insert(json.Length - 1, ",\"id\":\"org.example/duplicate\"");
        var missing = json.Replace("\"purpose\":\"Read one bounded workspace file after governance permits the operation.\",", string.Empty, StringComparison.Ordinal);
        var wrongType = json.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\"", StringComparison.Ordinal);
        var nestedUnknown = json.Replace("\"implementationId\":\"workspace/read-file\"", "\"implementationId\":\"workspace/read-file\",\"trusted\":true", StringComparison.Ordinal);
        var oversized = new string('x', CapabilityContractLimits.MaxDescriptorJsonCharacters + 1);

        AssertRejected(malformed, "invalid_descriptor_json");
        AssertRejected(duplicate, "duplicate_descriptor_property");
        AssertRejected(missing, "descriptor_property_required");
        AssertRejected(wrongType, "integer_required");
        AssertRejected(nestedUnknown, "unknown_descriptor_property");
        AssertRejected(oversized, "invalid_descriptor_json");
        AssertRejected("[]", "object_required");
    }

    [Fact]
    public void Every_closed_vocabulary_value_serializes_and_parses()
    {
        var valid = CapabilityContractTestData.ValidDescriptor();
        foreach (var kind in Enum.GetValues<CapabilityKind>().Where(value => value != CapabilityKind.Unknown))
        {
            AssertRoundTrip(valid with { Kind = kind });
        }

        foreach (var provenance in Enum.GetValues<CapabilityProvenanceKind>().Where(value => value != CapabilityProvenanceKind.Unknown))
        {
            AssertRoundTrip(valid with { Provenance = valid.Provenance with { Kind = provenance } });
        }

        foreach (var sideEffect in Enum.GetValues<CapabilitySideEffectClass>().Where(value => value != CapabilitySideEffectClass.Unknown))
        {
            AssertRoundTrip(valid with { SideEffectClass = sideEffect });
        }

        foreach (var egress in Enum.GetValues<CapabilityEgressMode>().Where(value => value != CapabilityEgressMode.Unknown))
        {
            var destinations = egress == CapabilityEgressMode.Restricted ? valid.Requirements.EgressDestinations : [];
            AssertRoundTrip(valid with { Requirements = WithEgress(valid.Requirements, egress, destinations) });
        }
    }

    [Fact]
    public void Closed_json_reader_returns_structured_field_errors_for_invalid_leaf_values()
    {
        Assert.True(CapabilityDescriptorJson.TrySerialize(CapabilityContractTestData.ValidDescriptor(), out var json, out _));
        var mutations = new (string Find, string Replace, string Code)[]
        {
            ("org.embodysense/workspace/read-file", "org.EmbodySense/workspace/read-file", "invalid_capability_id"),
            ("\"kind\":\"actuator\"", "\"kind\":\"ambient-tool\"", "unsupported_capability_kind"),
            ("1.2.3-beta.2\\u002Bbuild.7", "01.2.3", "invalid_capability_version"),
            ("\"providerId\":\"org.embodysense\"", "\"providerId\":\"Org.embodysense\"", "invalid_provider_id"),
            ("\"kind\":\"remote-artifact\"", "\"kind\":\"trusted-source\"", "unsupported_provenance_kind"),
            ("sha256:" + new string('a', 64), "sha256:" + new string('A', 64), "invalid_integrity_digest"),
            ("[1.0.0,2.0.0)", "1.0", "invalid_capability_version_range"),
            ("windows/x64", "Windows/x64", "invalid_capability_platform"),
            (CapabilityJsonSchema.Draft202012Dialect, "https://json-schema.org/draft/2019-09/schema", "unsupported_json_schema_dialect"),
            ("\"sideEffectClass\":\"read-only\"", "\"sideEffectClass\":\"ambient-write\"", "unsupported_side_effect_class"),
            ("\"egressMode\":\"restricted\"", "\"egressMode\":\"ambient\"", "unsupported_egress_mode"),
            ("workspace-content", "Bad Data", "invalid_data_class"),
            ("provider-token", "token=value", "invalid_secret_requirement"),
            ("\"api.example.com\"", "42", "invalid_collection_item"),
            ("\"purpose\":\"Read one bounded workspace file after governance permits the operation.\"", "\"purpose\":42", "string_required"),
            ("\"sourceRevision\":\"commit-123\"", "\"sourceRevision\":42", "string_required"),
            ("\"maxMemoryBytes\":134217728", "\"maxMemoryBytes\":\"134217728\"", "integer_required"),
            ("\"supportedPlatforms\":[\"linux/x64\",\"windows/x64\"]", "\"supportedPlatforms\":[]", "collection_out_of_range")
        };

        foreach (var (find, replace, code) in mutations)
        {
            var mutated = json!.Replace(find, replace, StringComparison.Ordinal);
            Assert.True(!string.Equals(json, mutated, StringComparison.Ordinal), $"Mutation source was not found: {find}");
            AssertRejected(mutated, code);
        }

        var validatorFailure = json!.Replace("\"purpose\":\"Read one bounded workspace file after governance permits the operation.\"", "\"purpose\":\" \"", StringComparison.Ordinal);
        AssertRejected(validatorFailure, "purpose_required");
    }

    [Fact]
    public void Lifecycle_axes_are_distinct_validated_server_owned_state()
    {
        var descriptor = CapabilityContractTestData.ValidDescriptor();
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        var snapshot = new CapabilityLifecycleSnapshot(
            CapabilityLifecycleSnapshot.CurrentSchemaVersion,
            identity!,
            CapabilityDeclarationState.Declared,
            CapabilityInstallationState.Installed,
            CapabilityEnablementState.Enabled,
            CapabilityHealthState.Healthy,
            CapabilityRetirementState.Active,
            CapabilityTrustState.Verified);

        Assert.True(CapabilityLifecycleSnapshotValidator.Validate(snapshot).IsValid);
        Assert.Equal(CapabilityDeclarationState.Declared, snapshot.Declaration);
        Assert.Equal(CapabilityInstallationState.Installed, snapshot.Installation);
        Assert.Equal(CapabilityEnablementState.Enabled, snapshot.Enablement);
        Assert.Equal(CapabilityHealthState.Healthy, snapshot.Health);
        Assert.Equal(CapabilityRetirementState.Active, snapshot.Retirement);
        Assert.Equal(CapabilityTrustState.Verified, snapshot.Trust);

        Assert.Contains(CapabilityLifecycleSnapshotValidator.Validate(null).Errors, error => error.Code == "lifecycle_snapshot_required");
        Assert.Contains(CapabilityLifecycleSnapshotValidator.Validate(snapshot with { SchemaVersion = 2 }).Errors, error => error.Code == "unsupported_schema_version");
        Assert.Contains(CapabilityLifecycleSnapshotValidator.Validate(snapshot with { DescriptorIdentity = null! }).Errors, error => error.Code == "descriptor_identity_required");
        Assert.Contains(CapabilityLifecycleSnapshotValidator.Validate(snapshot with { Health = (CapabilityHealthState)999 }).Errors, error => error.Code == "unsupported_lifecycle_state");

        Assert.True(CapabilityLifecycleSnapshotValidator.Validate(snapshot with { Declaration = CapabilityDeclarationState.Withdrawn }).IsValid);
        Assert.True(CapabilityLifecycleSnapshotValidator.Validate(snapshot with { Installation = CapabilityInstallationState.NotInstalled }).IsValid);
        Assert.True(CapabilityLifecycleSnapshotValidator.Validate(snapshot with { Enablement = CapabilityEnablementState.Disabled }).IsValid);
        Assert.True(CapabilityLifecycleSnapshotValidator.Validate(snapshot with { Health = CapabilityHealthState.Degraded }).IsValid);
        Assert.True(CapabilityLifecycleSnapshotValidator.Validate(snapshot with { Health = CapabilityHealthState.Unavailable }).IsValid);
        Assert.True(CapabilityLifecycleSnapshotValidator.Validate(snapshot with { Retirement = CapabilityRetirementState.Deprecated }).IsValid);
        Assert.True(CapabilityLifecycleSnapshotValidator.Validate(snapshot with { Retirement = CapabilityRetirementState.Removed }).IsValid);
        Assert.True(CapabilityLifecycleSnapshotValidator.Validate(snapshot with { Trust = CapabilityTrustState.Unverified }).IsValid);
        Assert.True(CapabilityLifecycleSnapshotValidator.Validate(snapshot with { Trust = CapabilityTrustState.Rejected }).IsValid);
    }

    private static void AssertRejected(string json, string expectedCode)
    {
        Assert.False(CapabilityDescriptorJson.TryDeserialize(json, out var descriptor, out var validation));
        Assert.Null(descriptor);
        Assert.Contains(validation.Errors, error => error.Code == expectedCode);
    }

    private static void AssertRoundTrip(CapabilityDescriptor descriptor)
    {
        Assert.True(CapabilityDescriptorJson.TrySerialize(descriptor, out var json, out var serialization), string.Join(Environment.NewLine, serialization.Errors));
        Assert.True(CapabilityDescriptorJson.TryDeserialize(json, out var parsed, out var deserialization), string.Join(Environment.NewLine, deserialization.Errors));
        Assert.NotNull(parsed);
    }

    private static CapabilityCompatibility WithPlatforms(CapabilityCompatibility value, IReadOnlyList<CapabilityPlatform> platforms)
    {
        return new CapabilityCompatibility(value.HostVersionRange, platforms);
    }

    private static CapabilityAccessRequirements WithDataClasses(CapabilityAccessRequirements value, IReadOnlyList<CapabilityDataClass> dataClasses)
    {
        return new CapabilityAccessRequirements(dataClasses, value.EgressMode, value.EgressDestinations, value.Secrets);
    }

    private static CapabilityAccessRequirements WithEgress(CapabilityAccessRequirements value, CapabilityEgressMode mode, IReadOnlyList<string> destinations)
    {
        return new CapabilityAccessRequirements(value.DataClasses, mode, destinations, value.Secrets);
    }

    private static CapabilityAccessRequirements WithEgressDestinations(CapabilityAccessRequirements value, IReadOnlyList<string> destinations)
    {
        return new CapabilityAccessRequirements(value.DataClasses, value.EgressMode, destinations, value.Secrets);
    }

    private static CapabilityAccessRequirements WithSecrets(CapabilityAccessRequirements value, IReadOnlyList<CapabilitySecretRequirement> secrets)
    {
        return new CapabilityAccessRequirements(value.DataClasses, value.EgressMode, value.EgressDestinations, secrets);
    }
}
