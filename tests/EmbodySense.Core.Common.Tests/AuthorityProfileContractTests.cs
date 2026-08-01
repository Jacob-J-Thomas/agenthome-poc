using System.Globalization;
using System.Text.Json;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Tests;

public sealed class AuthorityProfileContractTests
{
    [Fact]
    public void Valid_profile_canonicalizes_hashes_and_excludes_self_granting_fields_independent_of_culture_and_set_order()
    {
        var first = AuthorityContractTestData.Profile(
            capabilities: [AuthorityContractTestData.Identity("1.2.3"), AuthorityContractTestData.Identity("2.0.0")],
            dataClasses: [AuthorityContractTestData.DataClass("workspace-content"), AuthorityContractTestData.DataClass("user-content")],
            conditions: [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.HumanApprovalRequired), new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview)]);
        var reordered = AuthorityContractTestData.Profile(
            capabilities: [AuthorityContractTestData.Identity("2.0.0"), AuthorityContractTestData.Identity("1.2.3")],
            dataClasses: [AuthorityContractTestData.DataClass("user-content"), AuthorityContractTestData.DataClass("workspace-content")],
            conditions: [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview), new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.HumanApprovalRequired)]);
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.True(AuthorityProfileJson.TrySerialize(first, out var firstJson, out var firstValidation));
            Assert.True(AuthorityProfileJson.TrySerialize(reordered, out var reorderedJson, out var reorderedValidation));
            Assert.True(firstValidation.IsValid);
            Assert.True(reorderedValidation.IsValid);
            Assert.Equal(firstJson, reorderedJson);
            Assert.True(AuthorityProfileHash.TryCompute(first, out var firstHash, out _));
            Assert.True(AuthorityProfileHash.TryCompute(reordered, out var reorderedHash, out _));
            Assert.Equal(firstHash, reorderedHash);
            Assert.True(AuthorityProfileHash.TryParse(firstHash!.Value, out var parsedHash, out _));
            Assert.Equal(firstHash, parsedHash);
            Assert.True(firstHash.Equals((object)parsedHash!));
            Assert.False(firstHash.Equals((object)firstHash.Value));
            Assert.Equal(firstHash.Value, firstHash.ToString());

            using var document = JsonDocument.Parse(firstJson!);
            var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            Assert.DoesNotContain(names, name => name.Contains("trust", StringComparison.OrdinalIgnoreCase) || name.Contains("grant", StringComparison.OrdinalIgnoreCase) || name.Contains("approval", StringComparison.OrdinalIgnoreCase) || name.Contains("assignment", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase));
            Assert.StartsWith("{\"boundaryConditions\":", firstJson, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Closed_json_reader_round_trips_canonical_contracts_and_rejects_forged_malformed_duplicate_unknown_and_noncanonical_forms()
    {
        var profile = AuthorityContractTestData.Profile(conditions: [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview)]);
        Assert.True(AuthorityProfileJson.TrySerialize(profile, out var json, out _));
        Assert.True(AuthorityProfileJson.TryDeserialize(json, out var parsed, out var parsedValidation));
        Assert.True(parsedValidation.IsValid);
        Assert.NotNull(parsed);
        Assert.True(AuthorityProfileJson.TrySerialize(parsed, out var roundTrip, out _));
        Assert.Equal(json, roundTrip);
        var capabilityHash = profile.Ceiling.Capabilities[0].Hash.Value;

        var invalid = new (string Json, AuthorityContractErrorCode Code)[]
        {
            ("{", AuthorityContractErrorCode.InvalidJson),
            (json! + " ", AuthorityContractErrorCode.NonCanonicalJson),
            (json!.Insert(json.Length - 1, ",\"trust\":true"), AuthorityContractErrorCode.UnknownProperty),
            (json!.Insert(json.Length - 1, ",\"profileId\":\"other\""), AuthorityContractErrorCode.DuplicateProperty),
            (json!.Replace("\"purpose\":\"Inspect bounded workspace state for a user-directed support task.\",", string.Empty, StringComparison.Ordinal), AuthorityContractErrorCode.PropertyRequired),
            (json!.Replace("\"purpose\":\"Inspect bounded workspace state for a user-directed support task.\"", "\"purpose\":null", StringComparison.Ordinal), AuthorityContractErrorCode.StringRequired),
            (json!.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\"", StringComparison.Ordinal), AuthorityContractErrorCode.IntegerRequired),
            (json!.Replace("\"maxTargetCount\":5", "\"maxTargetCount\":5.0", StringComparison.Ordinal), AuthorityContractErrorCode.IntegerRequired),
            (json!.Replace(capabilityHash, "sha256:" + new string('z', 64), StringComparison.Ordinal), AuthorityContractErrorCode.CapabilityIdentityRequired),
            (json!.Replace("\"decision\":\"review\"", "\"decision\":\"approve\"", StringComparison.Ordinal), AuthorityContractErrorCode.InvalidBoundaryCondition),
            (json!.Replace("workspace-observer", "workspace\u202eobserver", StringComparison.Ordinal), AuthorityContractErrorCode.InvalidJson),
            (new string('x', AuthorityContractLimits.MaxProfileJsonCharacters + 1), AuthorityContractErrorCode.InvalidJson)
        };

        foreach (var (candidate, code) in invalid)
        {
            Assert.False(AuthorityProfileJson.TryDeserialize(candidate, out var rejected, out var validation));
            Assert.Null(rejected);
            Assert.Contains(validation.Errors, error => error.Code == code);
        }
    }

    [Fact]
    public void Every_closed_profile_provenance_ceiling_and_boundary_vocabulary_value_round_trips()
    {
        foreach (var status in Enum.GetValues<AuthorityProfileStatus>().Where(value => value != AuthorityProfileStatus.Unknown))
        {
            AssertRoundTrip(AuthorityContractTestData.Profile(status: status));
        }

        foreach (var kind in Enum.GetValues<AuthorityProvenanceKind>().Where(value => value != AuthorityProvenanceKind.Unknown))
        {
            var profile = AuthorityContractTestData.Profile() with { Provenance = new AuthorityProvenance(AuthorityContractTestData.ActorId("user-owner"), kind) };
            AssertRoundTrip(profile);
        }

        foreach (var sideEffectClass in Enum.GetValues<CapabilitySideEffectClass>().Where(value => value != CapabilitySideEffectClass.Unknown))
        {
            AssertRoundTrip(AuthorityContractTestData.Profile(maxSideEffectClass: sideEffectClass));
        }

        foreach (var condition in ValidConditions())
        {
            AssertRoundTrip(AuthorityContractTestData.Profile(conditions: [condition]));
        }
    }

    [Fact]
    public void Strict_reader_fails_closed_at_each_nested_contract_boundary()
    {
        var profile = AuthorityContractTestData.Profile(conditions: [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview)]);
        Assert.True(AuthorityProfileJson.TrySerialize(profile, out var json, out _));
        var capabilityHash = profile.Ceiling.Capabilities[0].Hash.Value;
        var candidates = new (string Find, string Replace, AuthorityContractErrorCode Code)[]
        {
            ("\"profileId\":\"workspace-observer\"", "\"profileId\":42", AuthorityContractErrorCode.StringRequired),
            ("\"profileId\":\"workspace-observer\"", "\"profileId\":\"Workspace\"", AuthorityContractErrorCode.InvalidProfileId),
            ("\"revision\":1", "\"revision\":\"1\"", AuthorityContractErrorCode.IntegerRequired),
            ("\"revision\":1", "\"revision\":0", AuthorityContractErrorCode.InvalidRevision),
            ("\"status\":\"active\"", "\"status\":42", AuthorityContractErrorCode.StringRequired),
            ("\"status\":\"active\"", "\"status\":\"trusted\"", AuthorityContractErrorCode.UnsupportedStatus),
            ("\"purpose\":\"Inspect bounded workspace state for a user-directed support task.\"", "\"purpose\":\" \"", AuthorityContractErrorCode.InvalidPurpose),
            ("\"provenance\":{\"actorId\":\"user-owner\",\"kind\":\"user-declaration\"}", "\"provenance\":42", AuthorityContractErrorCode.ObjectRequired),
            ("\"actorId\":\"user-owner\"", "\"actorId\":\"User\"", AuthorityContractErrorCode.InvalidActorId),
            ("\"kind\":\"user-declaration\"", "\"kind\":\"trust-record\"", AuthorityContractErrorCode.UnsupportedProvenanceKind),
            ("\"maxSideEffectClass\":\"read-only\"", "\"maxSideEffectClass\":\"ambient-write\"", AuthorityContractErrorCode.UnsupportedSideEffectClass),
            (capabilityHash, "sha256:" + new string('z', 64), AuthorityContractErrorCode.CapabilityIdentityRequired),
            ("\"workspace-content\"", "42", AuthorityContractErrorCode.CollectionItemRequired),
            ("\"decision\":\"review\"", "\"decision\":42", AuthorityContractErrorCode.StringRequired),
            ("\"allowsRecurrence\":false", "\"allowsRecurrence\":42", AuthorityContractErrorCode.BooleanRequired)
        };

        foreach (var (find, replace, code) in candidates)
        {
            var candidate = json!.Replace(find, replace, StringComparison.Ordinal);
            Assert.True(!string.Equals(json, candidate, StringComparison.Ordinal), $"Mutation source {find} was not found.");
            Assert.False(AuthorityProfileJson.TryDeserialize(candidate, out _, out var validation), $"Expected rejection for {code} mutation {find}.");
            Assert.Contains(validation.Errors, error => error.Code == code);
        }

        var issuedAtPrefix = "\"issuedAtUtc\":";
        var issuedAtStart = json!.IndexOf(issuedAtPrefix, StringComparison.Ordinal) + issuedAtPrefix.Length;
        var issuedAtEnd = json.IndexOf(',', issuedAtStart);
        var invalidIssuedAt = json[..issuedAtStart] + "null" + json[issuedAtEnd..];
        Assert.False(AuthorityProfileJson.TryDeserialize(invalidIssuedAt, out _, out var issuedAtValidation));
        Assert.Contains(issuedAtValidation.Errors, error => error.Code == AuthorityContractErrorCode.InvalidTimestamp);

        var emptyProfile = AuthorityContractTestData.Profile(capabilities: [], dataClasses: [], conditions: []);
        Assert.True(AuthorityProfileJson.TrySerialize(emptyProfile, out var emptyJson, out _));
        foreach (var (find, replace) in new[] { ("\"capabilities\":[]", "\"capabilities\":42"), ("\"dataClasses\":[]", "\"dataClasses\":42"), ("\"boundaryConditions\":[]", "\"boundaryConditions\":42") })
        {
            Assert.False(AuthorityProfileJson.TryDeserialize(emptyJson!.Replace(find, replace, StringComparison.Ordinal), out _, out var validation));
            Assert.Contains(validation.Errors, error => error.Code == AuthorityContractErrorCode.ArrayRequired);
        }
    }

    [Fact]
    public void Value_parsers_reject_noncanonical_unsafe_and_oversized_inputs_without_echoing_them()
    {
        var invalidProfiles = new[] { null, string.Empty, "Workspace", ".workspace", "workspace.", "workspace/child", "workspace\u202e", new string('a', AuthorityContractLimits.MaxProfileIdCharacters + 1) };
        foreach (var value in invalidProfiles)
        {
            Assert.False(AuthorityProfileId.TryParse(value, out _, out var error));
            Assert.Equal(AuthorityContractErrorCode.InvalidProfileId, error?.Code);
            Assert.Equal(AuthorityContractField.ProfileId, error?.Field);
        }

        foreach (var value in new[] { null, "0", "01", "-1", " 1", "1 ", "999999999999" })
        {
            Assert.False(AuthorityProfileRevision.TryParse(value, out _, out var error));
            Assert.Equal(AuthorityContractErrorCode.InvalidRevision, error?.Code);
        }

        foreach (var value in new[] { null, "User", "user/owner", "unsafe\u202e", new string('u', AuthorityContractLimits.MaxActorIdCharacters + 1) })
        {
            Assert.False(AuthorityActorId.TryParse(value, out _, out var error));
            Assert.Equal(AuthorityContractErrorCode.InvalidActorId, error?.Code);
        }

        foreach (var value in new[] { null, string.Empty, "Cafe\u0301", "unsafe\u202e", "\ud800", new string('p', AuthorityContractLimits.MaxPurposeCharacters + 1) })
        {
            Assert.False(AuthorityPurpose.TryParse(value, out _, out var error));
            Assert.Equal(AuthorityContractErrorCode.InvalidPurpose, error?.Code);
        }

        Assert.False(AuthorityProfileHash.TryParse("sha256:" + new string('A', 64), out _, out var hashError));
        Assert.Equal(AuthorityContractField.Contract, hashError?.Field);
    }

    [Fact]
    public void Validator_fails_closed_for_missing_forged_ambiguous_and_oversized_contract_values()
    {
        var valid = AuthorityContractTestData.Profile();
        var values = new (AuthorityProfile Profile, AuthorityContractErrorCode Code)[]
        {
            (valid with { SchemaVersion = 2 }, AuthorityContractErrorCode.UnsupportedSchemaVersion),
            (valid with { ProfileId = null! }, AuthorityContractErrorCode.Required),
            (valid with { Revision = null! }, AuthorityContractErrorCode.Required),
            (valid with { Purpose = null! }, AuthorityContractErrorCode.Required),
            (valid with { Status = AuthorityProfileStatus.Unknown }, AuthorityContractErrorCode.UnsupportedStatus),
            (valid with { Status = (AuthorityProfileStatus)999 }, AuthorityContractErrorCode.UnsupportedStatus),
            (valid with { Provenance = null! }, AuthorityContractErrorCode.Required),
            (valid with { Provenance = valid.Provenance with { ActorId = null! } }, AuthorityContractErrorCode.Required),
            (valid with { Provenance = valid.Provenance with { Kind = AuthorityProvenanceKind.Unknown } }, AuthorityContractErrorCode.UnsupportedProvenanceKind),
            (valid with { IssuedAtUtc = valid.IssuedAtUtc.ToOffset(TimeSpan.FromHours(1)) }, AuthorityContractErrorCode.InvalidTimestamp),
            (valid with { ExpiresAtUtc = valid.IssuedAtUtc }, AuthorityContractErrorCode.InvalidTimestamp),
            (valid with { Ceiling = null! }, AuthorityContractErrorCode.CeilingRequired),
            (WithCeiling(valid, new AuthorityCeiling([null!], valid.Ceiling.DataClasses, valid.Ceiling.MaxTargetCount, valid.Ceiling.MaxSideEffectClass, valid.Ceiling.AllowsRecurrence, valid.Ceiling.AllowsExternalPublication, valid.Ceiling.AllowsIrreversibleAction)), AuthorityContractErrorCode.CapabilityIdentityRequired),
            (WithCeiling(valid, new AuthorityCeiling(Enumerable.Repeat(AuthorityContractTestData.Identity(), AuthorityContractLimits.MaxCapabilitiesPerCeiling + 1).ToArray(), valid.Ceiling.DataClasses, valid.Ceiling.MaxTargetCount, valid.Ceiling.MaxSideEffectClass, valid.Ceiling.AllowsRecurrence, valid.Ceiling.AllowsExternalPublication, valid.Ceiling.AllowsIrreversibleAction)), AuthorityContractErrorCode.CollectionOutOfRange),
            (WithCeiling(valid, new AuthorityCeiling(valid.Ceiling.Capabilities, [null!], valid.Ceiling.MaxTargetCount, valid.Ceiling.MaxSideEffectClass, valid.Ceiling.AllowsRecurrence, valid.Ceiling.AllowsExternalPublication, valid.Ceiling.AllowsIrreversibleAction)), AuthorityContractErrorCode.CollectionItemRequired),
            (WithCeiling(valid, new AuthorityCeiling(valid.Ceiling.Capabilities, [AuthorityContractTestData.DataClass("workspace-content"), AuthorityContractTestData.DataClass("workspace-content")], valid.Ceiling.MaxTargetCount, valid.Ceiling.MaxSideEffectClass, valid.Ceiling.AllowsRecurrence, valid.Ceiling.AllowsExternalPublication, valid.Ceiling.AllowsIrreversibleAction)), AuthorityContractErrorCode.DuplicateCollectionItem),
            (WithCeiling(valid, new AuthorityCeiling(valid.Ceiling.Capabilities, Enumerable.Range(0, AuthorityContractLimits.MaxDataClassesPerCeiling + 1).Select(index => AuthorityContractTestData.DataClass($"workspace-content-{index}")).ToArray(), valid.Ceiling.MaxTargetCount, valid.Ceiling.MaxSideEffectClass, valid.Ceiling.AllowsRecurrence, valid.Ceiling.AllowsExternalPublication, valid.Ceiling.AllowsIrreversibleAction)), AuthorityContractErrorCode.CollectionOutOfRange),
            (valid with { Ceiling = valid.Ceiling with { MaxTargetCount = -1 } }, AuthorityContractErrorCode.TargetCountOutOfRange),
            (valid with { Ceiling = valid.Ceiling with { MaxTargetCount = AuthorityContractLimits.MaxTargetCount + 1 } }, AuthorityContractErrorCode.TargetCountOutOfRange),
            (valid with { Ceiling = valid.Ceiling with { MaxSideEffectClass = CapabilitySideEffectClass.Unknown } }, AuthorityContractErrorCode.UnsupportedSideEffectClass),
            (WithConditions(valid, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Unknown, AuthorityBoundaryReason.NoBoundary)]), AuthorityContractErrorCode.UnsupportedBoundaryDecision),
            (WithConditions(valid, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.Unknown)]), AuthorityContractErrorCode.UnsupportedBoundaryReason),
            (WithConditions(valid, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.MandatoryReview)]), AuthorityContractErrorCode.InvalidBoundaryCondition),
            (WithConditions(valid, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview), new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview)]), AuthorityContractErrorCode.DuplicateCollectionItem),
            (WithConditions(valid, Enumerable.Repeat(new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Deny, AuthorityBoundaryReason.Recurrence), AuthorityContractLimits.MaxBoundaryConditionsPerProfile + 1).ToArray()), AuthorityContractErrorCode.CollectionOutOfRange)
        };

        Assert.Contains(AuthorityProfileValidator.Validate(null).Errors, error => error.Code == AuthorityContractErrorCode.Required && error.Field == AuthorityContractField.Contract);
        foreach (var (profile, code) in values)
        {
            var validation = AuthorityProfileValidator.Validate(profile);
            Assert.False(validation.IsValid);
            Assert.Contains(validation.Errors, error => error.Code == code);
            Assert.False(AuthorityProfileJson.TrySerialize(profile, out var json, out var serialized));
            Assert.Null(json);
            Assert.False(serialized.IsValid);
        }
    }

    [Fact]
    public void Ceiling_and_profile_collections_are_defensive_snapshots()
    {
        var capabilities = new List<CapabilityDescriptorIdentity> { AuthorityContractTestData.Identity() };
        var dataClasses = new List<CapabilityDataClass> { AuthorityContractTestData.DataClass("workspace-content") };
        var conditions = new List<AuthorityBoundaryCondition> { new(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview) };
        var ceiling = new AuthorityCeiling(capabilities, dataClasses, 5, CapabilitySideEffectClass.ReadOnly, false, false, false);
        var profile = AuthorityContractTestData.Profile(capabilities: capabilities, dataClasses: dataClasses, conditions: conditions);

        capabilities.Clear();
        dataClasses.Clear();
        conditions.Clear();

        Assert.Single(ceiling.Capabilities);
        Assert.Single(ceiling.DataClasses);
        Assert.Single(profile.BoundaryConditions);
        Assert.Throws<NotSupportedException>(() => ((IList<CapabilityDescriptorIdentity>)ceiling.Capabilities).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<AuthorityBoundaryCondition>)profile.BoundaryConditions).Clear());
    }

    [Fact]
    public void Receipt_factory_closes_and_bounds_public_decision_evidence()
    {
        var profile = AuthorityContractTestData.Profile();
        var reference = new AuthorityProfileReference(profile.ProfileId, profile.Revision);
        var conditions = new List<AuthorityBoundaryCondition> { new(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary) };
        var profiles = new List<AuthorityProfileReference> { reference };

        Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(AuthorityBoundaryReceipt.CurrentSchemaVersion, AuthorityBoundaryDecision.Direct, conditions, profiles, AuthorityContractTestData.IssuedAtUtc, out var receipt, out var validation));
        Assert.True(validation.IsValid);
        conditions.Clear();
        profiles.Clear();
        Assert.Single(receipt!.Conditions);
        Assert.Single(receipt.Profiles);
        Assert.Throws<NotSupportedException>(() => ((IList<AuthorityBoundaryCondition>)receipt.Conditions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<AuthorityProfileReference>)receipt.Profiles).Clear());

        AssertReceiptRejected(2, AuthorityBoundaryDecision.Direct, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)], [reference], AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.UnsupportedSchemaVersion);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Unknown, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)], [reference], AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.UnsupportedBoundaryDecision);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Review, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)], [reference], AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.InvalidBoundaryCondition);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Direct, null, [reference], AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.CollectionOutOfRange);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Direct, [], [reference], AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.CollectionOutOfRange);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Direct, Enumerable.Repeat(new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary), AuthorityContractLimits.MaxBoundaryConditionsPerReceipt + 1).ToArray(), [reference], AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.CollectionOutOfRange);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Direct, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.Unknown)], [reference], AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.UnsupportedBoundaryReason);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Direct, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary), new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)], [reference], AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.DuplicateCollectionItem);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Review, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary), new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview)], [reference], AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.InvalidBoundaryCondition);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Pause, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary), new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Pause, AuthorityBoundaryReason.StaleEvidence)], [reference], AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.InvalidBoundaryCondition);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Deny, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary), new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Deny, AuthorityBoundaryReason.Recurrence)], [reference], AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.InvalidBoundaryCondition);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Direct, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)], null, AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.CollectionOutOfRange);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Direct, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)], [], AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.InvalidIntersectionProfiles);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Direct, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)], Enumerable.Repeat(reference, AuthorityContractLimits.MaxProfilesPerIntersection + 1).ToArray(), AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.CollectionOutOfRange);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Direct, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)], [reference, reference], AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.DuplicateProfileRevision);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Direct, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)], [new AuthorityProfileReference(null!, profile.Revision)], AuthorityContractTestData.IssuedAtUtc, AuthorityContractErrorCode.CollectionItemRequired);
        AssertReceiptRejected(1, AuthorityBoundaryDecision.Direct, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)], [reference], AuthorityContractTestData.IssuedAtUtc.ToOffset(TimeSpan.FromHours(1)), AuthorityContractErrorCode.InvalidEvaluationTime);
        Assert.False(AuthorityBoundaryReceiptFactory.Validate(null).IsValid);
    }

    private static void AssertReceiptRejected(
        int schemaVersion,
        AuthorityBoundaryDecision decision,
        IReadOnlyList<AuthorityBoundaryCondition>? conditions,
        IReadOnlyList<AuthorityProfileReference>? profiles,
        DateTimeOffset evaluatedAtUtc,
        AuthorityContractErrorCode expectedCode)
    {
        Assert.False(AuthorityBoundaryReceiptFactory.TryCreate(schemaVersion, decision, conditions, profiles, evaluatedAtUtc, out var receipt, out var validation));
        Assert.Null(receipt);
        Assert.Contains(validation.Errors, error => error.Code == expectedCode);
    }

    private static AuthorityProfile WithCeiling(AuthorityProfile profile, AuthorityCeiling ceiling)
    {
        return new AuthorityProfile(profile.SchemaVersion, profile.ProfileId, profile.Revision, profile.Status, profile.Purpose, profile.Provenance, profile.IssuedAtUtc, profile.ExpiresAtUtc, ceiling, profile.BoundaryConditions);
    }

    private static AuthorityProfile WithConditions(AuthorityProfile profile, IReadOnlyList<AuthorityBoundaryCondition> conditions)
    {
        return new AuthorityProfile(profile.SchemaVersion, profile.ProfileId, profile.Revision, profile.Status, profile.Purpose, profile.Provenance, profile.IssuedAtUtc, profile.ExpiresAtUtc, profile.Ceiling, conditions);
    }

    private static IEnumerable<AuthorityBoundaryCondition> ValidConditions()
    {
        yield return new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary);
        yield return new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview);
        yield return new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.HumanApprovalRequired);
        foreach (var reason in new[] { AuthorityBoundaryReason.ProfileDraft, AuthorityBoundaryReason.ProfileSuspended, AuthorityBoundaryReason.StaleEvidence, AuthorityBoundaryReason.ConflictingState, AuthorityBoundaryReason.UncertainUserIntent })
        {
            yield return new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Pause, reason);
        }

        foreach (var reason in new[] { AuthorityBoundaryReason.ProfileRetired, AuthorityBoundaryReason.ProfileExpired, AuthorityBoundaryReason.InvalidContract, AuthorityBoundaryReason.TargetLimitExceeded, AuthorityBoundaryReason.DataClassExceeded, AuthorityBoundaryReason.SideEffectExceeded, AuthorityBoundaryReason.ExternalPublication, AuthorityBoundaryReason.IrreversibleAction, AuthorityBoundaryReason.Recurrence })
        {
            yield return new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Deny, reason);
        }
    }

    private static void AssertRoundTrip(AuthorityProfile profile)
    {
        Assert.True(AuthorityProfileJson.TrySerialize(profile, out var json, out var serialization), string.Join(',', serialization.Errors));
        Assert.True(AuthorityProfileJson.TryDeserialize(json, out var parsed, out var deserialization), string.Join(',', deserialization.Errors));
        Assert.NotNull(parsed);
    }
}
