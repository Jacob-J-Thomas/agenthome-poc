using System.Globalization;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Tests;

public sealed class AuthorityCeilingIntersectionTests
{
    [Fact]
    public void Intersection_is_commutative_idempotent_monotone_and_culture_independent()
    {
        var identityOne = AuthorityContractTestData.Identity("1.2.3");
        var identityTwo = AuthorityContractTestData.Identity("2.0.0");
        var workspace = AuthorityContractTestData.DataClass("workspace-content");
        var user = AuthorityContractTestData.DataClass("user-content");
        var first = AuthorityContractTestData.Profile("first", capabilities: [identityOne, identityTwo], dataClasses: [workspace, user], maxTargetCount: 9, maxSideEffectClass: CapabilitySideEffectClass.ExternalReversible, allowsRecurrence: true, allowsExternalPublication: true, allowsIrreversibleAction: true);
        var second = AuthorityContractTestData.Profile("second", capabilities: [identityOne], dataClasses: [workspace], maxTargetCount: 3, maxSideEffectClass: CapabilitySideEffectClass.ReadOnly, allowsRecurrence: false, allowsExternalPublication: true, allowsIrreversibleAction: false);
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var forward = AuthorityCeilingIntersection.Evaluate([first, second], AuthorityContractTestData.IssuedAtUtc.AddMinutes(1));
            var reverse = AuthorityCeilingIntersection.Evaluate([second, first], AuthorityContractTestData.IssuedAtUtc.AddMinutes(1));
            var singleProfile = AuthorityCeilingIntersection.Evaluate([first], AuthorityContractTestData.IssuedAtUtc.AddMinutes(1));

            Assert.True(forward.Validation.IsValid);
            Assert.Equal(AuthorityBoundaryDecision.Direct, forward.Receipt.Decision);
            AssertCeilingEqual(forward.CandidateCeiling, reverse.CandidateCeiling);
            AssertCeilingEqual(forward.EffectiveCeiling, reverse.EffectiveCeiling);
            Assert.Equal(new[] { identityOne }, forward.EffectiveCeiling.Capabilities);
            Assert.Equal(new[] { workspace }, forward.EffectiveCeiling.DataClasses);
            Assert.Equal(3, forward.EffectiveCeiling.MaxTargetCount);
            Assert.Equal(CapabilitySideEffectClass.ReadOnly, forward.EffectiveCeiling.MaxSideEffectClass);
            Assert.False(forward.EffectiveCeiling.AllowsRecurrence);
            Assert.True(forward.EffectiveCeiling.AllowsExternalPublication);
            Assert.False(forward.EffectiveCeiling.AllowsIrreversibleAction);
            AssertCeilingEqual(first.Ceiling, singleProfile.CandidateCeiling);
            AssertCeilingEqual(first.Ceiling, singleProfile.EffectiveCeiling);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Intersection_handles_distinct_descriptor_hashes_for_the_same_capability_id_and_version()
    {
        var firstIdentity = AuthorityContractTestData.Identity();
        var descriptor = CapabilityContractTestData.ValidDescriptor() with { Purpose = "Read one bounded workspace file with a distinct descriptor declaration." };
        Assert.True(EmbodySense.Core.Common.Capabilities.CapabilityDescriptorIdentity.TryCreate(descriptor, out var secondIdentity, out var validation), string.Join(',', validation.Errors));
        Assert.NotNull(secondIdentity);
        Assert.Equal(firstIdentity.Id, secondIdentity.Id);
        Assert.Equal(firstIdentity.Version, secondIdentity.Version);
        Assert.NotEqual(firstIdentity.Hash, secondIdentity.Hash);

        var first = AuthorityContractTestData.Profile("first", capabilities: [firstIdentity]);
        var second = AuthorityContractTestData.Profile("second", capabilities: [secondIdentity]);
        var result = AuthorityCeilingIntersection.Evaluate([first, second], AuthorityContractTestData.IssuedAtUtc.AddSeconds(1));

        Assert.True(result.Validation.IsValid);
        Assert.Empty(result.CandidateCeiling.Capabilities);
        Assert.Empty(result.EffectiveCeiling.Capabilities);
    }

    [Fact]
    public void Singleton_intersection_canonicalizes_ceiling_collection_ordering()
    {
        var earlier = AuthorityContractTestData.Identity("1.0.0");
        var later = AuthorityContractTestData.Identity("2.0.0");
        var user = AuthorityContractTestData.DataClass("user-content");
        var workspace = AuthorityContractTestData.DataClass("workspace-content");
        var profile = AuthorityContractTestData.Profile(capabilities: [later, earlier], dataClasses: [workspace, user]);

        var result = AuthorityCeilingIntersection.Evaluate([profile], AuthorityContractTestData.IssuedAtUtc.AddSeconds(1));

        Assert.Equal(new[] { earlier, later }, result.CandidateCeiling.Capabilities);
        Assert.Equal(new[] { earlier, later }, result.EffectiveCeiling.Capabilities);
        Assert.Equal(new[] { user, workspace }, result.CandidateCeiling.DataClasses);
        Assert.Equal(new[] { user, workspace }, result.EffectiveCeiling.DataClasses);
    }

    [Fact]
    public void Property_style_intersections_never_widen_each_input_and_never_union_collections()
    {
        var random = new Random(231);
        var identities = new[] { AuthorityContractTestData.Identity("1.2.3"), AuthorityContractTestData.Identity("2.0.0"), AuthorityContractTestData.Identity("3.0.0") };
        var dataClasses = new[] { AuthorityContractTestData.DataClass("workspace-content"), AuthorityContractTestData.DataClass("user-content"), AuthorityContractTestData.DataClass("project-metadata") };

        for (var iteration = 0; iteration < 96; iteration++)
        {
            var first = RandomProfile("first", random, identities, dataClasses);
            var second = RandomProfile("second", random, identities, dataClasses);
            var result = AuthorityCeilingIntersection.Evaluate([first, second], AuthorityContractTestData.IssuedAtUtc.AddSeconds(iteration));

            Assert.True(result.Validation.IsValid);
            Assert.All(result.CandidateCeiling.Capabilities, identity =>
            {
                Assert.Contains(identity, first.Ceiling.Capabilities);
                Assert.Contains(identity, second.Ceiling.Capabilities);
            });
            Assert.All(result.CandidateCeiling.DataClasses, dataClass =>
            {
                Assert.Contains(dataClass, first.Ceiling.DataClasses);
                Assert.Contains(dataClass, second.Ceiling.DataClasses);
            });
            Assert.True(result.CandidateCeiling.MaxTargetCount <= first.Ceiling.MaxTargetCount);
            Assert.True(result.CandidateCeiling.MaxTargetCount <= second.Ceiling.MaxTargetCount);
            Assert.True((int)result.CandidateCeiling.MaxSideEffectClass <= (int)first.Ceiling.MaxSideEffectClass);
            Assert.True((int)result.CandidateCeiling.MaxSideEffectClass <= (int)second.Ceiling.MaxSideEffectClass);
            Assert.True(!result.CandidateCeiling.AllowsRecurrence || first.Ceiling.AllowsRecurrence && second.Ceiling.AllowsRecurrence);
            Assert.True(!result.CandidateCeiling.AllowsExternalPublication || first.Ceiling.AllowsExternalPublication && second.Ceiling.AllowsExternalPublication);
            Assert.True(!result.CandidateCeiling.AllowsIrreversibleAction || first.Ceiling.AllowsIrreversibleAction && second.Ceiling.AllowsIrreversibleAction);
        }
    }

    [Theory]
    [InlineData(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary, AuthorityBoundaryDecision.Direct)]
    [InlineData(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview, AuthorityBoundaryDecision.Review)]
    [InlineData(AuthorityBoundaryDecision.Pause, AuthorityBoundaryReason.StaleEvidence, AuthorityBoundaryDecision.Pause)]
    [InlineData(AuthorityBoundaryDecision.Deny, AuthorityBoundaryReason.TargetLimitExceeded, AuthorityBoundaryDecision.Deny)]
    public void Boundary_conditions_produce_exact_nonexecuting_decisions(AuthorityBoundaryDecision conditionDecision, AuthorityBoundaryReason reason, AuthorityBoundaryDecision expected)
    {
        var profile = AuthorityContractTestData.Profile(conditions: [new AuthorityBoundaryCondition(conditionDecision, reason)]);
        var result = AuthorityCeilingIntersection.Evaluate([profile], AuthorityContractTestData.IssuedAtUtc.AddSeconds(1));

        Assert.True(result.Validation.IsValid);
        Assert.Equal(expected, result.Receipt.Decision);
        Assert.Contains(result.Receipt.Conditions, condition => condition.Decision == conditionDecision && condition.Reason == reason);
        if (expected == AuthorityBoundaryDecision.Direct)
        {
            AssertCeilingEqual(profile.Ceiling, result.EffectiveCeiling);
        }
        else
        {
            AssertEmptyCeiling(result.EffectiveCeiling);
        }
    }

    [Fact]
    public void Decision_precedence_expiry_and_lifecycle_statuses_fail_closed_with_exact_reasons()
    {
        var precedence = AuthorityContractTestData.Profile(conditions: [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview), new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Pause, AuthorityBoundaryReason.StaleEvidence), new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Deny, AuthorityBoundaryReason.Recurrence)]);
        var result = AuthorityCeilingIntersection.Evaluate([precedence], AuthorityContractTestData.IssuedAtUtc.AddSeconds(1));
        Assert.Equal(AuthorityBoundaryDecision.Deny, result.Receipt.Decision);
        AssertEmptyCeiling(result.EffectiveCeiling);

        var expiring = AuthorityContractTestData.Profile(expiresAtUtc: AuthorityContractTestData.IssuedAtUtc.AddMinutes(1));
        var before = AuthorityCeilingIntersection.Evaluate([expiring], AuthorityContractTestData.IssuedAtUtc.AddSeconds(59));
        var endpoint = AuthorityCeilingIntersection.Evaluate([expiring], AuthorityContractTestData.IssuedAtUtc.AddMinutes(1));
        Assert.Equal(AuthorityBoundaryDecision.Direct, before.Receipt.Decision);
        Assert.Equal(AuthorityBoundaryDecision.Deny, endpoint.Receipt.Decision);
        Assert.Contains(endpoint.Receipt.Conditions, condition => condition.Reason == AuthorityBoundaryReason.ProfileExpired);
        Assert.Contains(endpoint.Validation.Errors, error => error.Code == AuthorityContractErrorCode.Expired);

        Assert.Equal(AuthorityBoundaryDecision.Pause, AuthorityCeilingIntersection.Evaluate([AuthorityContractTestData.Profile(status: AuthorityProfileStatus.Draft)], AuthorityContractTestData.IssuedAtUtc.AddSeconds(1)).Receipt.Decision);
        Assert.Equal(AuthorityBoundaryDecision.Pause, AuthorityCeilingIntersection.Evaluate([AuthorityContractTestData.Profile(status: AuthorityProfileStatus.Suspended)], AuthorityContractTestData.IssuedAtUtc.AddSeconds(1)).Receipt.Decision);
        Assert.Equal(AuthorityBoundaryDecision.Deny, AuthorityCeilingIntersection.Evaluate([AuthorityContractTestData.Profile(status: AuthorityProfileStatus.Retired)], AuthorityContractTestData.IssuedAtUtc.AddSeconds(1)).Receipt.Decision);
    }

    [Theory]
    [InlineData(AuthorityProfileStatus.Active, AuthorityBoundaryDecision.Pause)]
    [InlineData(AuthorityProfileStatus.Draft, AuthorityBoundaryDecision.Pause)]
    [InlineData(AuthorityProfileStatus.Suspended, AuthorityBoundaryDecision.Pause)]
    [InlineData(AuthorityProfileStatus.Retired, AuthorityBoundaryDecision.Deny)]
    public void Profiles_are_not_effective_before_their_inclusive_issue_time(AuthorityProfileStatus status, AuthorityBoundaryDecision beforeIssueDecision)
    {
        var profile = AuthorityContractTestData.Profile(status: status, expiresAtUtc: AuthorityContractTestData.IssuedAtUtc.AddMinutes(1));

        var beforeIssue = AuthorityCeilingIntersection.Evaluate([profile], AuthorityContractTestData.IssuedAtUtc.AddTicks(-1));
        var atIssue = AuthorityCeilingIntersection.Evaluate([profile], AuthorityContractTestData.IssuedAtUtc);
        var afterIssue = AuthorityCeilingIntersection.Evaluate([profile], AuthorityContractTestData.IssuedAtUtc.AddTicks(1));
        var atExpiry = AuthorityCeilingIntersection.Evaluate([profile], AuthorityContractTestData.IssuedAtUtc.AddMinutes(1));
        var issuedDecision = status switch
        {
            AuthorityProfileStatus.Active => AuthorityBoundaryDecision.Direct,
            AuthorityProfileStatus.Draft or AuthorityProfileStatus.Suspended => AuthorityBoundaryDecision.Pause,
            AuthorityProfileStatus.Retired => AuthorityBoundaryDecision.Deny,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

        Assert.Equal(beforeIssueDecision, beforeIssue.Receipt.Decision);
        Assert.Contains(beforeIssue.Receipt.Conditions, condition => condition.Decision == AuthorityBoundaryDecision.Pause && condition.Reason == AuthorityBoundaryReason.StaleEvidence);
        AssertEmptyCeiling(beforeIssue.EffectiveCeiling);
        Assert.Equal(issuedDecision, atIssue.Receipt.Decision);
        Assert.Equal(issuedDecision, afterIssue.Receipt.Decision);
        Assert.DoesNotContain(atIssue.Receipt.Conditions, condition => condition.Reason == AuthorityBoundaryReason.StaleEvidence);
        Assert.DoesNotContain(afterIssue.Receipt.Conditions, condition => condition.Reason == AuthorityBoundaryReason.StaleEvidence);
        Assert.Equal(AuthorityBoundaryDecision.Deny, atExpiry.Receipt.Decision);
        Assert.Contains(atExpiry.Receipt.Conditions, condition => condition.Reason == AuthorityBoundaryReason.ProfileExpired);
        AssertEmptyCeiling(atExpiry.EffectiveCeiling);
        if (status == AuthorityProfileStatus.Active)
        {
            AssertCeilingEqual(profile.Ceiling, atIssue.EffectiveCeiling);
            AssertCeilingEqual(profile.Ceiling, afterIssue.EffectiveCeiling);
        }
        else
        {
            AssertEmptyCeiling(atIssue.EffectiveCeiling);
            AssertEmptyCeiling(afterIssue.EffectiveCeiling);
        }
    }

    [Fact]
    public void Direct_markers_do_not_create_contradictory_restrictive_receipt_evidence()
    {
        var direct = AuthorityContractTestData.Profile("direct", conditions: [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)]);
        var review = AuthorityContractTestData.Profile("review", conditions: [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview)]);

        var result = AuthorityCeilingIntersection.Evaluate([direct, review], AuthorityContractTestData.IssuedAtUtc.AddSeconds(1));

        Assert.True(result.Validation.IsValid);
        Assert.Equal(AuthorityBoundaryDecision.Review, result.Receipt.Decision);
        Assert.Equal([new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview)], result.Receipt.Conditions);
        AssertEmptyCeiling(result.EffectiveCeiling);
    }

    [Fact]
    public void Distinct_profile_identities_and_provenance_do_not_union_capability_authority_or_change_monotone_intersection()
    {
        var firstIdentity = AuthorityContractTestData.Identity("1.2.3");
        var secondIdentity = AuthorityContractTestData.Identity("2.0.0");
        var first = AuthorityContractTestData.Profile("profile-a", capabilities: [firstIdentity]);
        var imported = AuthorityContractTestData.Profile("profile-b", capabilities: [secondIdentity]);
        imported = imported with { Provenance = new AuthorityProvenance(AuthorityContractTestData.ActorId("import-operator"), AuthorityProvenanceKind.ImportedArtifact) };

        var result = AuthorityCeilingIntersection.Evaluate([first, imported], AuthorityContractTestData.IssuedAtUtc.AddSeconds(1));

        Assert.True(result.Validation.IsValid);
        Assert.Equal(AuthorityBoundaryDecision.Direct, result.Receipt.Decision);
        Assert.Empty(result.CandidateCeiling.Capabilities);
        Assert.Empty(result.EffectiveCeiling.Capabilities);
        Assert.Equal(new[] { "profile-a", "profile-b" }, result.Receipt.Profiles.Select(reference => reference.ProfileId.Value));
        Assert.DoesNotContain(result.Receipt.Profiles, reference => reference.ProfileId.Value == imported.Provenance.ActorId.Value);
    }

    [Fact]
    public void Duplicate_or_conflicting_revisions_fail_closed_before_intersection_and_receipts_deduplicate_references()
    {
        var profile = AuthorityContractTestData.Profile("same-revision");
        var variants = new[]
        {
            profile,
            profile with { Purpose = AuthorityContractTestData.Purpose("A conflicting declared purpose.") },
            profile with { Provenance = new AuthorityProvenance(AuthorityContractTestData.ActorId("different-actor"), AuthorityProvenanceKind.ImportedArtifact) },
            profile with { Ceiling = profile.Ceiling with { MaxTargetCount = profile.Ceiling.MaxTargetCount - 1 } }
        };

        foreach (var variant in variants)
        {
            var result = AuthorityCeilingIntersection.Evaluate([profile, variant], AuthorityContractTestData.IssuedAtUtc.AddSeconds(1));

            Assert.False(result.Validation.IsValid);
            Assert.Contains(result.Validation.Errors, error => error.Code == AuthorityContractErrorCode.DuplicateProfileRevision && error.Field == AuthorityContractField.Profiles);
            Assert.Equal(AuthorityBoundaryDecision.Deny, result.Receipt.Decision);
            AssertEmptyCeiling(result.CandidateCeiling);
            AssertEmptyCeiling(result.EffectiveCeiling);
            Assert.Single(result.Receipt.Profiles);
            Assert.Equal(profile.ProfileId, result.Receipt.Profiles[0].ProfileId);
            Assert.Equal(profile.Revision, result.Receipt.Profiles[0].Revision);
        }
    }

    [Fact]
    public void Missing_invalid_or_ambiguous_inputs_produce_zero_effective_ceiling_and_value_free_receipts()
    {
        var valid = AuthorityContractTestData.Profile();
        var invalid = new AuthorityProfile(valid.SchemaVersion, valid.ProfileId, valid.Revision, valid.Status, valid.Purpose, valid.Provenance, valid.IssuedAtUtc, valid.ExpiresAtUtc, valid.Ceiling, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.MandatoryReview)]);
        foreach (var result in new[]
        {
            AuthorityCeilingIntersection.Evaluate(null, AuthorityContractTestData.IssuedAtUtc),
            AuthorityCeilingIntersection.Evaluate([], AuthorityContractTestData.IssuedAtUtc),
            AuthorityCeilingIntersection.Evaluate(Enumerable.Repeat(valid, AuthorityContractLimits.MaxProfilesPerIntersection + 1).ToArray(), AuthorityContractTestData.IssuedAtUtc),
            AuthorityCeilingIntersection.Evaluate([invalid], AuthorityContractTestData.IssuedAtUtc),
            AuthorityCeilingIntersection.Evaluate([valid], AuthorityContractTestData.IssuedAtUtc.ToOffset(TimeSpan.FromHours(1)))
        })
        {
            Assert.Equal(AuthorityBoundaryDecision.Deny, result.Receipt.Decision);
            AssertEmptyCeiling(result.EffectiveCeiling);
            Assert.Contains(result.Receipt.Conditions, condition => condition.Reason == AuthorityBoundaryReason.InvalidContract);
            Assert.False(result.Validation.IsValid);
        }
    }

    private static AuthorityProfile RandomProfile(string profileId, Random random, IReadOnlyList<EmbodySense.Core.Common.Capabilities.CapabilityDescriptorIdentity> identities, IReadOnlyList<EmbodySense.Core.Common.Capabilities.CapabilityDataClass> dataClasses)
    {
        return AuthorityContractTestData.Profile(
            profileId,
            capabilities: identities.Where(_ => random.Next(2) == 0).ToArray(),
            dataClasses: dataClasses.Where(_ => random.Next(2) == 0).ToArray(),
            maxTargetCount: random.Next(0, AuthorityContractLimits.MaxTargetCount + 1),
            maxSideEffectClass: (CapabilitySideEffectClass)random.Next((int)CapabilitySideEffectClass.None, (int)CapabilitySideEffectClass.Irreversible + 1),
            allowsRecurrence: random.Next(2) == 0,
            allowsExternalPublication: random.Next(2) == 0,
            allowsIrreversibleAction: random.Next(2) == 0);
    }

    private static void AssertEmptyCeiling(AuthorityCeiling ceiling)
    {
        AssertCeilingEqual(AuthorityCeilingIntersection.EmptyCeiling(), ceiling);
    }

    private static void AssertCeilingEqual(AuthorityCeiling expected, AuthorityCeiling actual)
    {
        Assert.Equal(expected.Capabilities.OrderBy(value => value.Id).ThenBy(value => value.Version).ThenBy(value => value.Hash.Value, StringComparer.Ordinal), actual.Capabilities.OrderBy(value => value.Id).ThenBy(value => value.Version).ThenBy(value => value.Hash.Value, StringComparer.Ordinal));
        Assert.Equal(expected.DataClasses.OrderBy(value => value), actual.DataClasses.OrderBy(value => value));
        Assert.Equal(expected.MaxTargetCount, actual.MaxTargetCount);
        Assert.Equal(expected.MaxSideEffectClass, actual.MaxSideEffectClass);
        Assert.Equal(expected.AllowsRecurrence, actual.AllowsRecurrence);
        Assert.Equal(expected.AllowsExternalPublication, actual.AllowsExternalPublication);
        Assert.Equal(expected.AllowsIrreversibleAction, actual.AllowsIrreversibleAction);
    }
}
