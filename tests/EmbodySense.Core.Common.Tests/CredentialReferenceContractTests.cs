using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Common.Tests;

public sealed class CredentialReferenceContractTests
{
    [Fact]
    public void Reference_json_is_schema_1_canonical_bounded_and_round_trips()
    {
        var mutable = new Dictionary<string, string> { ["service"] = "Example", ["display-name"] = "Example token" };
        var reference = CredentialContractTestData.Reference(mutable);
        mutable["service"] = "Changed";

        Assert.True(CredentialContractJson.TrySerialize(reference, out var first, out var validation), string.Join(';', validation.Errors.Select(error => error.Message)));
        Assert.True(CredentialContractJson.TryDeserializeReference(first, out var parsed, out validation), string.Join(';', validation.Errors.Select(error => error.Message)));
        Assert.True(CredentialContractJson.TrySerialize(parsed, out var second, out validation));
        Assert.Equal(first, second);
        Assert.Equal(1, parsed!.SchemaVersion);
        Assert.Equal("Example", parsed.Metadata["service"]);
        Assert.Contains("\"metadata\":{\"display-name\":\"Example token\",\"service\":\"Example\"}", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Changed", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Reference_parser_rejects_unknown_duplicate_reordered_and_noncanonical_json()
    {
        Assert.True(CredentialContractJson.TrySerialize(CredentialContractTestData.Reference(), out var canonical, out _));
        var variants = new[]
        {
            canonical!.Replace("{", "{\"unknown\":true,", StringComparison.Ordinal),
            canonical.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
            " " + canonical,
            canonical.Replace("\"schemaVersion\":1,\"id\"", "\"id\":\"credential-1\",\"schemaVersion\":1,\"discard\"", StringComparison.Ordinal)
        };

        foreach (var variant in variants)
        {
            Assert.False(CredentialContractJson.TryDeserializeReference(variant, out var parsed, out _));
            Assert.Null(parsed);
        }
    }

    [Fact]
    public void Reference_validation_rejects_unsafe_or_authority_like_metadata_and_bounds()
    {
        var unsafePurpose = CredentialContractTestData.Reference() with { Purpose = "unsafe\u202e" };
        var forgedTrust = CredentialContractTestData.Reference(new Dictionary<string, string> { ["trusted"] = "true" });
        var tooMany = CredentialContractTestData.Reference(Enumerable.Range(0, CredentialContractLimits.MaxMetadataEntries + 1).ToDictionary(index => $"item-{index}", _ => "value"));
        var invalidStatus = CredentialContractTestData.Reference() with { Status = (CredentialLifecycleStatus)99 };

        Assert.False(CredentialContractValidator.Validate(unsafePurpose).IsValid);
        Assert.Contains(CredentialContractValidator.Validate(forgedTrust).Errors, error => error.Code == CredentialContractErrorCode.MetadataKeyNotAllowed);
        Assert.Contains(CredentialContractValidator.Validate(tooMany).Errors, error => error.Code == CredentialContractErrorCode.InvalidMetadata);
        Assert.Contains(CredentialContractValidator.Validate(invalidStatus).Errors, error => error.Code == CredentialContractErrorCode.InvalidLifecycleStatus);
    }

    [Fact]
    public void Credential_identifiers_are_exact_lowercase_ascii_and_hashes_compare_in_fixed_time()
    {
        var id = CredentialContractTestData.ReferenceId("credential-1");
        Assert.Equal(id, CredentialContractTestData.ReferenceId("credential-1"));
        Assert.True(id.Equals((object)CredentialContractTestData.ReferenceId("credential-1")));
        Assert.False(id.Equals((object)"credential-1"));
        Assert.Equal(1, id.CompareTo(null));
        Assert.Equal("credential-1", id.ToString());

        foreach (var invalid in new string?[] { null, "", "Credential-1", "credential value", "credential\u202e", new string('a', CredentialContractLimits.MaxIdCharacters + 1) })
        {
            Assert.False(CredentialReferenceId.TryParse(invalid, out _, out _));
            Assert.False(CredentialContractId.TryParse(invalid, out _, out _));
        }

        Assert.False(CredentialProviderId.TryParse("provider", out _, out _));
        Assert.False(CredentialProviderId.TryParse("Org.Example", out _, out _));
        var hash = CredentialContractHash.Compute("canonical");
        Assert.True(hash.FixedTimeEquals(CredentialContractHash.Compute("canonical")));
        Assert.False(hash.FixedTimeEquals(CredentialContractHash.Compute("different")));
        Assert.True(CredentialContractHash.TryParse(hash.Value, out var parsed, out _));
        Assert.Equal(hash, parsed);
        Assert.Equal(hash.Value, hash.ToString());
    }
}
