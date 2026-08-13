using System.Globalization;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Common.Tests.Triggers;

public sealed class TriggerLoopReferenceContractTests
{
    [Fact]
    public void Factories_create_exact_closed_arms_and_governed_factory_defensively_copies_pins()
    {
        var legacy = TriggerDeliveryTestData.Loop();
        Assert.Equal(TriggerLoopTargetKind.LegacyDefinition, legacy.Kind);
        Assert.Equal("loop-1", legacy.LoopId);
        Assert.Equal(3, legacy.DefinitionVersion);
        Assert.Equal(new string('b', 64), legacy.ContentHash);
        Assert.NotNull(legacy.LegacyDefinition);
        Assert.Null(legacy.GovernedPublication);
        Assert.Null(legacy.AuthorityGrant);

        var publication = Publication();
        var grant = Grant();
        Assert.True(TriggerDeliveryFactory.TryCreateGovernedLoopReference(publication, grant, out var governed, out var validation));
        Assert.True(validation.IsValid);
        Assert.Equal(TriggerLoopTargetKind.GovernedPublication, governed!.Kind);
        Assert.Equal(publication.Revision.GraphId, governed.LoopId);
        Assert.Null(governed.LegacyDefinition);
        Assert.Null(governed.DefinitionVersion);
        Assert.Null(governed.ContentHash);
        Assert.Equal(publication, governed.GovernedPublication);
        Assert.Equal(grant, governed.AuthorityGrant);
        Assert.NotSame(publication, governed.GovernedPublication);
        Assert.NotSame(publication.Revision, governed.GovernedPublication!.Revision);
        Assert.NotSame(grant, governed.AuthorityGrant);
        Assert.NotSame(grant.GrantId, governed.AuthorityGrant!.GrantId);
        Assert.NotSame(grant.Revision, governed.AuthorityGrant.Revision);
        Assert.Equal(TriggerDeliveryTestData.GovernedLoop(), governed);
    }

    [Fact]
    public void Validator_and_factories_reject_null_and_malformed_arm_inputs()
    {
        var publication = Publication();
        var grant = Grant();
        var result = TriggerDeliveryValidator.ValidateLoopReference(null);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "invalid_loop_reference");
        Assert.False(TriggerLoopReferenceHash.TryCompute(null, out var hash, out _));
        Assert.Null(hash);
        Assert.False(TriggerDeliveryFactory.TryCreateLoopReference(null, 1, new string('a', 64), out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateLoopReference("../loop", 1, new string('a', 64), out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateLoopReference("loop", 0, new string('a', 64), out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateLoopReference("loop", 1, new string('A', 64), out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateGovernedLoopReference(null, grant, out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateGovernedLoopReference(publication, null, out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateGovernedLoopReference(publication with { PublicationOperationId = "../publish" }, grant, out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateGovernedLoopReference(publication, grant with { GrantId = null! }, out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateGovernedLoopReference(publication, grant with { ContentHash = new string('f', 64) }, out _, out _));
    }

    [Fact]
    public void Canonical_json_round_trips_both_arms_with_one_ordered_shape_and_no_competing_loop_identity()
    {
        var legacyEnvelope = TriggerDeliveryTestData.Envelope();
        Assert.True(TriggerDeliveryJson.TrySerialize(legacyEnvelope, out var legacyJson, out _));
        var legacyFragment = LegacyFragment();
        Assert.Contains(legacyFragment, legacyJson, StringComparison.Ordinal);
        Assert.Equal(1, Count(legacyJson!, "\"definitionVersion\""));
        Assert.True(TriggerDeliveryJson.TryDeserialize(legacyJson, out var parsedLegacy, out _));
        Assert.Equal(legacyEnvelope.Loop, parsedLegacy!.Loop);

        var governedEnvelope = TriggerDeliveryTestData.Envelope(loop: TriggerDeliveryTestData.GovernedLoop());
        Assert.True(TriggerDeliveryJson.TrySerialize(governedEnvelope, out var governedJson, out _));
        Assert.Contains(GovernedFragment(), governedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"loopId\"", governedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"definitionVersion\"", governedJson, StringComparison.Ordinal);
        Assert.True(TriggerDeliveryJson.TryDeserialize(governedJson, out var parsedGoverned, out _));
        Assert.Equal(governedEnvelope.Loop, parsedGoverned!.Loop);
        Assert.Equal("graph-1", parsedGoverned.Loop.LoopId);
    }

    [Fact]
    public void Parser_rejects_old_untagged_unknown_hybrid_partial_duplicate_and_noncanonical_union_documents()
    {
        Assert.True(TriggerDeliveryJson.TrySerialize(TriggerDeliveryTestData.Envelope(), out var legacyJson, out _));
        var legacyFragment = LegacyFragment();
        var oldUntagged = legacyJson!.Replace(legacyFragment, "\"loop\":{\"contentHash\":\"" + new string('b', 64) + "\",\"definitionVersion\":3,\"loopId\":\"loop-1\"}", StringComparison.Ordinal);
        var unknown = legacyJson.Replace("\"kind\":\"legacy-definition\"", "\"kind\":\"unknown\"", StringComparison.Ordinal);
        var hybridGrant = legacyJson.Replace("\"authorityGrant\":null", "\"authorityGrant\":" + GrantJson(), StringComparison.Ordinal);
        var missingLegacy = legacyJson.Replace("\"legacyDefinition\":" + LegacyJson(), "\"legacyDefinition\":null", StringComparison.Ordinal);
        var duplicateVersion = legacyJson.Replace("\"definitionVersion\":3", "\"definitionVersion\":3,\"definitionVersion\":3", StringComparison.Ordinal);
        var reordered = legacyJson.Replace("\"kind\":\"legacy-definition\",\"legacyDefinition\":" + LegacyJson(), "\"legacyDefinition\":" + LegacyJson() + ",\"kind\":\"legacy-definition\"", StringComparison.Ordinal);

        AssertRejected(oldUntagged, "invalid_json_shape");
        AssertRejected(unknown, "invalid_json_shape");
        AssertRejected(hybridGrant, "invalid_json_shape");
        AssertRejected(missingLegacy, "invalid_json_shape");
        AssertRejected(duplicateVersion, "invalid_json_shape");
        AssertRejected(reordered, "noncanonical_json");

        Assert.True(TriggerDeliveryJson.TrySerialize(TriggerDeliveryTestData.Envelope(loop: TriggerDeliveryTestData.GovernedLoop()), out var governedJson, out _));
        var missingGrant = governedJson!.Replace("\"authorityGrant\":" + GrantJson(), "\"authorityGrant\":null", StringComparison.Ordinal);
        var missingPublication = governedJson.Replace("\"governedPublication\":" + PublicationJson(), "\"governedPublication\":null", StringComparison.Ordinal);
        var hybridLegacy = governedJson.Replace("\"legacyDefinition\":null", "\"legacyDefinition\":" + LegacyJson(), StringComparison.Ordinal);
        var malformedGrantRevision = governedJson.Replace("\"revision\":2},\"governedPublication\"", "\"revision\":0},\"governedPublication\"", StringComparison.Ordinal);
        var duplicateKind = governedJson.Replace("\"kind\":\"governed-publication\"", "\"kind\":\"governed-publication\",\"kind\":\"governed-publication\"", StringComparison.Ordinal);

        AssertRejected(missingGrant, "invalid_json_shape");
        AssertRejected(missingPublication, "invalid_json_shape");
        AssertRejected(hybridLegacy, "invalid_json_shape");
        AssertRejected(malformedGrantRevision, "invalid_json_shape");
        AssertRejected(duplicateKind, "invalid_json_shape");
    }

    [Fact]
    public void Reference_hash_is_stable_domain_separated_and_changes_for_every_arm_identity_field()
    {
        var baseline = TriggerDeliveryTestData.Loop();
        Assert.True(TriggerLoopReferenceHash.TryCompute(baseline, out var baselineHash, out var validation));
        Assert.True(validation.IsValid);
        Assert.Matches("^[0-9a-f]{64}$", baselineHash!);
        Assert.True(TriggerLoopReferenceHash.TryCompute(TriggerDeliveryTestData.Loop(), out var equalHash, out _));
        Assert.Equal(baselineHash, equalHash);
        Assert.True(TriggerDeliveryHash.TryCompute(TriggerDeliveryTestData.Envelope(loop: baseline), out var envelopeHash, out _));
        Assert.NotEqual(envelopeHash, baselineHash);

        var legacyVariants = new[]
        {
            TriggerDeliveryTestData.Loop(id: "loop-2"),
            TriggerDeliveryTestData.Loop(version: 4),
            TriggerDeliveryTestData.Loop(hashCharacter: 'f')
        };

        foreach (var variant in legacyVariants)
        {
            Assert.True(TriggerLoopReferenceHash.TryCompute(variant, out var hash, out _));
            Assert.NotEqual(baselineHash, hash);
        }

        var governed = TriggerDeliveryTestData.GovernedLoop();
        Assert.True(TriggerLoopReferenceHash.TryCompute(governed, out var governedHash, out _));
        Assert.NotEqual(baselineHash, governedHash);
        var governedVariants = new[]
        {
            TriggerDeliveryTestData.GovernedLoop(graphId: "graph-2"),
            TriggerDeliveryTestData.GovernedLoop(revisionId: "revision-4"),
            TriggerDeliveryTestData.GovernedLoop(executableHash: 'f'),
            TriggerDeliveryTestData.GovernedLoop(publicationOperationId: "publish-4"),
            TriggerDeliveryTestData.GovernedLoop(validationHash: 'f'),
            TriggerDeliveryTestData.GovernedLoop(grantId: "grant-2"),
            TriggerDeliveryTestData.GovernedLoop(grantRevision: 3),
            TriggerDeliveryTestData.GovernedLoop(grantHash: 'f')
        };

        foreach (var variant in governedVariants)
        {
            Assert.True(TriggerLoopReferenceHash.TryCompute(variant, out var hash, out _));
            Assert.NotEqual(governedHash, hash);
        }

        Assert.True(TriggerLoopReferenceHash.TryCompute(TriggerDeliveryTestData.GovernedLoop(), out var equalGovernedHash, out _));
        Assert.Equal(governedHash, equalGovernedHash);
    }

    [Fact]
    public void Governed_arm_accepts_exact_identifier_bounds_and_contract_shape_contains_no_secret_material()
    {
        var graphId = new string('g', TriggerDeliveryLimits.MaxLoopIdCharacters);
        var operationId = new string('p', GovernedLoopRevisionContractLimits.MaxIdentifierCharacters);
        var grantId = new string('a', AuthorityGrantContractLimits.MaxGrantIdCharacters);
        var atBounds = TriggerDeliveryTestData.GovernedLoop(graphId: graphId, publicationOperationId: operationId, grantId: grantId, grantRevision: int.MaxValue);
        Assert.True(TriggerDeliveryValidator.ValidateLoopReference(atBounds).IsValid);
        Assert.Equal(graphId, atBounds.LoopId);

        var oversizedOperation = atBounds.GovernedPublication! with { PublicationOperationId = operationId + "p" };
        Assert.False(TriggerDeliveryFactory.TryCreateGovernedLoopReference(oversizedOperation, atBounds.AuthorityGrant, out _, out _));
        Assert.DoesNotContain(typeof(TriggerLoopReference).GetProperties(), property => property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.True(TriggerDeliveryJson.TrySerialize(TriggerDeliveryTestData.Envelope(loop: atBounds), out var json, out _));
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    private static GovernedLoopRevisionPublicationPin Publication()
    {
        var revision = GovernedLoopRevisionReference.Create(1, "graph-1", "revision-3", new string('c', 64));
        return GovernedLoopRevisionPublicationPinFactory.Create(1, revision, "publish-3", new string('d', 64));
    }

    private static AuthorityGrantReference Grant()
    {
        Assert.True(AuthorityGrantId.TryParse("grant-1", out var grantId, out _));
        Assert.True(AuthorityGrantRevision.TryParse("2", out var revision, out _));
        return new AuthorityGrantReference(grantId!, revision!, "sha256:" + new string('e', 64));
    }

    private static string LegacyFragment() => "\"loop\":" + LegacyUnionJson();

    private static string LegacyUnionJson() => "{\"authorityGrant\":null,\"governedPublication\":null,\"kind\":\"legacy-definition\",\"legacyDefinition\":" + LegacyJson() + "}";

    private static string LegacyJson() => "{\"contentHash\":\"" + new string('b', 64) + "\",\"definitionVersion\":3,\"loopId\":\"loop-1\"}";

    private static string GovernedFragment() => "\"loop\":{\"authorityGrant\":" + GrantJson() + ",\"governedPublication\":" + PublicationJson() + ",\"kind\":\"governed-publication\",\"legacyDefinition\":null}";

    private static string GrantJson() => "{\"contentHash\":\"sha256:" + new string('e', 64) + "\",\"grantId\":\"grant-1\",\"revision\":2}";

    private static string PublicationJson() => "{\"executableHash\":\"" + new string('c', 64) + "\",\"graphId\":\"graph-1\",\"publicationOperationId\":\"publish-3\",\"revisionId\":\"revision-3\",\"schemaVersion\":1,\"validationEvidenceHash\":\"" + new string('d', 64) + "\"}";

    private static int Count(string value, string expected)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(expected, index, StringComparison.Ordinal)) >= 0; index += expected.Length)
        {
            count++;
        }

        return count;
    }

    private static void AssertRejected(string json, string expectedCode)
    {
        Assert.False(TriggerDeliveryJson.TryDeserialize(json, out var envelope, out var validation));
        Assert.Null(envelope);
        Assert.Contains(validation.Errors, error => error.Code == expectedCode);
    }
}
