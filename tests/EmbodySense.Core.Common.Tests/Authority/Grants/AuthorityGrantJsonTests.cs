using System.Globalization;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Tests.Authority.Grants;

public sealed class AuthorityGrantJsonTests
{
    [Fact]
    public void Canonical_json_round_trips_every_field_and_sorts_set_like_ceiling_collections()
    {
        var firstCapability = AuthorityGrantTestFixture.Capability("org.embodysense/workspace/write-file", "2.0.0", 'f');
        var secondCapability = AuthorityGrantTestFixture.Capability();
        var grant = AuthorityGrantTestFixture.Grant(
            ceiling: AuthorityGrantTestFixture.Ceiling(
                [firstCapability, secondCapability],
                [AuthorityGrantTestFixture.DataClass("workspace-content"), AuthorityGrantTestFixture.DataClass("user-content")],
                17,
                CapabilitySideEffectClass.LocalReversible,
                true,
                true,
                false),
            boundary: AuthorityGrantTestFixture.Boundary(completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion));

        Assert.True(AuthorityGrantJson.TrySerialize(grant, out var json, out var serialization), Describe(serialization));
        Assert.True(AuthorityGrantJson.TryDeserialize(json, out var parsed, out var deserialization), Describe(deserialization));
        Assert.NotNull(parsed);
        Assert.Equal(grant.ContentHash, parsed.ContentHash);
        Assert.Equal(grant.Binding, parsed.Binding);
        Assert.Equal(grant.Boundary, parsed.Boundary);
        Assert.Equal(secondCapability, parsed.RequestedCeiling.Capabilities[0]);
        Assert.Equal("user-content", parsed.RequestedCeiling.DataClasses[0].Value);
        Assert.True(AuthorityGrantJson.TrySerialize(parsed, out var roundTrip, out _));
        Assert.Equal(json, roundTrip);
        Assert.StartsWith("{\"binding\":", json, StringComparison.Ordinal);
        Assert.Contains("\"completionConstraint\":\"first-bound-run-completion\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"active\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialization_is_culture_independent_and_collection_order_independent()
    {
        var capabilityA = AuthorityGrantTestFixture.Capability();
        var capabilityB = AuthorityGrantTestFixture.Capability("org.embodysense/workspace/write-file", "2.0.0", 'f');
        var first = AuthorityGrantTestFixture.Grant(ceiling: AuthorityGrantTestFixture.Ceiling([capabilityA, capabilityB], [AuthorityGrantTestFixture.DataClass("user-content"), AuthorityGrantTestFixture.DataClass("workspace-content")]));
        var reordered = AuthorityGrantTestFixture.Grant(ceiling: AuthorityGrantTestFixture.Ceiling([capabilityB, capabilityA], [AuthorityGrantTestFixture.DataClass("workspace-content"), AuthorityGrantTestFixture.DataClass("user-content")]));
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(first.ContentHash, reordered.ContentHash);
            Assert.True(AuthorityGrantJson.TrySerialize(first, out var firstJson, out _));
            Assert.True(AuthorityGrantJson.TrySerialize(reordered, out var secondJson, out _));
            Assert.Equal(firstJson, secondJson);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Closed_reader_rejects_malformed_unknown_duplicate_case_alias_noncanonical_and_oversized_json()
    {
        var grant = AuthorityGrantTestFixture.Grant();
        Assert.True(AuthorityGrantJson.TrySerialize(grant, out var json, out _));

        var invalid = new (string Candidate, AuthorityGrantValidationErrorCode Code)[]
        {
            ("{", AuthorityGrantValidationErrorCode.InvalidJson),
            (json! + " ", AuthorityGrantValidationErrorCode.NonCanonicalJson),
            (json!.Insert(json.Length - 1, ",\"trust\":true"), AuthorityGrantValidationErrorCode.InvalidJson),
            (json!.Insert(json.Length - 1, ",\"grantId\":\"other\""), AuthorityGrantValidationErrorCode.InvalidJson),
            (json!.Replace("\"grantId\":\"workspace-helper\"", "\"GrantId\":\"workspace-helper\"", StringComparison.Ordinal), AuthorityGrantValidationErrorCode.InvalidJson),
            (json!.Replace("\"status\":\"active\"", "\"status\":\"Active\"", StringComparison.Ordinal), AuthorityGrantValidationErrorCode.InvalidJson),
            (json!.Replace("\"schemaVersion\":1", "\"schemaVersion\":1.0", StringComparison.Ordinal), AuthorityGrantValidationErrorCode.InvalidJson),
            (json!.Replace("\"revision\":1", "\"revision\":1e0", StringComparison.Ordinal), AuthorityGrantValidationErrorCode.InvalidJson),
            (json!.Replace($"\"contentHash\":\"{grant.ContentHash}\"", $"\"contentHash\":\"sha256:{new string('0', 64)}\"", StringComparison.Ordinal), AuthorityGrantValidationErrorCode.InvalidHash),
            (json!.Replace("\"effectiveAtUtc\":", "\"effectiveAtUtc\":null,\"unused\":", StringComparison.Ordinal), AuthorityGrantValidationErrorCode.InvalidJson),
            (json!.Replace("workspace-helper", "workspace\u202ehelper", StringComparison.Ordinal), AuthorityGrantValidationErrorCode.InvalidJson),
            (new string('x', AuthorityGrantContractLimits.MaxGrantJsonCharacters + 1), AuthorityGrantValidationErrorCode.InvalidJson),
        };

        foreach (var (candidate, code) in invalid)
        {
            Assert.False(AuthorityGrantJson.TryDeserialize(candidate, out var rejected, out var validation), candidate[..Math.Min(candidate.Length, 80)]);
            Assert.Null(rejected);
            Assert.True(validation.Errors.Any(error => error.Code == code), $"Expected {code} for {candidate[..Math.Min(candidate.Length, 120)]}");
        }
    }

    [Fact]
    public void Reader_rejects_wrong_nested_types_missing_properties_invalid_pins_and_noncanonical_timestamps()
    {
        var grant = AuthorityGrantTestFixture.Grant();
        Assert.True(AuthorityGrantJson.TrySerialize(grant, out var json, out _));
        var mutations = new (string Find, string Replace)[]
        {
            ("\"binding\":{", "\"binding\":42,"),
            ("\"profileId\":\"default-profile\"", "\"profileId\":null"),
            ("\"roleId\":\"bounded-helper\"", "\"roleId\":\"Invalid/Role\""),
            ("\"graphId\":\"governed-loop\"", "\"graphId\":\"not/filename-safe\""),
            ("\"requestedCeiling\":{", "\"requestedCeiling\":[] ,\"discard\":"),
            ("\"capabilities\":[]", "\"capabilities\":{}"),
            ("\"dataClasses\":[\"workspace-content\"]", "\"dataClasses\":[42]"),
            ("\"maxTargetCount\":5", "\"maxTargetCount\":\"5\""),
            ("\"allowsRecurrence\":false", "\"allowsRecurrence\":0"),
            ("\"effectiveAtUtc\":\"2026-08-10T11:55:00.0000000\\u002B00:00\"", "\"effectiveAtUtc\":\"2026-08-10T06:55:00.0000000-05:00\""),
        };

        foreach (var (find, replace) in mutations)
        {
            var candidate = json!.Replace(find, replace, StringComparison.Ordinal);
            Assert.True(!string.Equals(json, candidate, StringComparison.Ordinal), $"Mutation source was not found: {find}");
            Assert.False(AuthorityGrantJson.TryDeserialize(candidate, out _, out var validation), find);
            Assert.Contains(validation.Errors, error => error.Code is AuthorityGrantValidationErrorCode.InvalidJson or AuthorityGrantValidationErrorCode.InvalidBoundary or AuthorityGrantValidationErrorCode.InvalidIdentity);
        }

        var missingReason = json!.Replace("\"reason\":\"Delegate bounded work for one governed loop revision.\",", string.Empty, StringComparison.Ordinal);
        Assert.False(AuthorityGrantJson.TryDeserialize(missingReason, out _, out var missingValidation));
        Assert.Contains(missingValidation.Errors, error => error.Code == AuthorityGrantValidationErrorCode.InvalidJson);
    }

    [Fact]
    public void Serializer_rejects_null_invalid_and_forged_hash_contracts()
    {
        Assert.False(AuthorityGrantJson.TrySerialize(null, out var nullJson, out var nullValidation));
        Assert.Null(nullJson);
        Assert.Contains(nullValidation.Errors, error => error.Code == AuthorityGrantValidationErrorCode.Required);

        var grant = AuthorityGrantTestFixture.Grant();
        Assert.False(AuthorityGrantJson.TrySerialize(grant with { ContentHash = "sha256:" + new string('0', 64) }, out var forgedJson, out var forgedValidation));
        Assert.Null(forgedJson);
        Assert.Contains(forgedValidation.Errors, error => error.Code == AuthorityGrantValidationErrorCode.InvalidHash);
    }

    private static string Describe(AuthorityGrantValidationResult result)
        => string.Join(';', result.Errors.Select(error => $"{error.Code}:{error.Path}"));
}
