using System.Text.Json;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Tests.Authority.Grants;

namespace EmbodySense.Core.Common.Tests.Loops.Admission;

public sealed class GovernedLoopAdmissionRejectionProofTests
{
    [Fact]
    public void Every_rejection_reference_is_exactly_derived_and_cannot_be_recertified_after_substitution()
    {
        foreach (var failureCode in Enum.GetValues<GovernedLoopAdmissionFailureCode>().Where(value => value != GovernedLoopAdmissionFailureCode.None))
        {
            var valid = GovernedLoopAdmissionTestFixture.Rejection(failureCode: failureCode);
            for (var index = 0; index < valid.References.Count; index++)
            {
                var references = valid.References.ToArray();
                references[index] = references[index] with { EvidenceHash = DifferentHash(references[index].EvidenceHash) };
                var substituted = NewRejection(valid, references: references);

                Assert.False(GovernedLoopAdmissionValidator.Validate(substituted).IsValid);
                Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.Apply(substituted));
            }

            Assert.Equal(
                valid.References,
                GovernedLoopAdmissionContractHash.CreateRejectionEvidenceReferences(
                    valid.Intent,
                    valid.FailureCode,
                    valid.AuthorityDenial,
                    valid.CapabilityDenial));

            if (valid.AuthorityDenial is not null)
            {
                var effectiveAuthority = Assert.Single(valid.References, reference => reference.Kind == GovernedLoopAdmissionEvidenceKind.EffectiveAuthority);
                Assert.Equal(
                    GovernedLoopAdmissionContractHash.ComputeAuthorityCeilingReferenceHash(valid.AuthorityDenial.EffectiveCeiling),
                    effectiveAuthority.EvidenceHash);
            }
        }
    }

    [Fact]
    public void Stale_references_fail_after_intent_or_structured_proof_changes()
    {
        var roleFailure = GovernedLoopAdmissionTestFixture.Rejection(failureCode: GovernedLoopAdmissionFailureCode.RoleMismatch);
        var changedIntent = roleFailure.Intent with { GraphArtifactHash = GovernedLoopAdmissionTestFixture.Hash('9') };
        var staleIntent = NewRejection(roleFailure, intent: changedIntent);

        var authorityFailure = GovernedLoopAdmissionTestFixture.Rejection(failureCode: GovernedLoopAdmissionFailureCode.AuthorityDenied);
        var authorityProof = authorityFailure.AuthorityDenial!;
        var changedAuthorityProof = new GovernedLoopAdmissionAuthorityDenialProof(
            authorityProof.SchemaVersion,
            WithMaxTargets(authorityProof.CandidateCeiling, authorityProof.CandidateCeiling.MaxTargetCount + 1),
            authorityProof.EffectiveCeiling,
            authorityProof.BoundaryReceipt);
        var staleAuthorityProof = NewRejection(authorityFailure, authorityDenial: changedAuthorityProof);

        var capabilityFailure = GovernedLoopAdmissionTestFixture.Rejection(failureCode: GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied);
        var capabilityProof = capabilityFailure.CapabilityDenial!;
        var changedCapabilityProof = new GovernedLoopAdmissionCapabilityDenialProof(
            capabilityProof.SchemaVersion,
            capabilityProof.Requirements,
            capabilityProof.RequirementsHash,
            WithMaxTargets(capabilityProof.EffectiveAuthority, capabilityProof.EffectiveAuthority.MaxTargetCount + 1),
            capabilityProof.Violations,
            capabilityProof.EvaluatedAtUtc);
        var staleCapabilityProof = NewRejection(capabilityFailure, capabilityDenial: changedCapabilityProof);

        Assert.All(
            new[] { staleIntent, staleCapabilityProof },
            candidate =>
            {
                Assert.False(GovernedLoopAdmissionValidator.Validate(candidate).IsValid);
                Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.Apply(candidate));
            });

        var reboundAuthority = Rebind(authorityFailure, authorityDenial: changedAuthorityProof);
        var reboundCapability = Rebind(capabilityFailure, capabilityDenial: changedCapabilityProof);
        Assert.False(GovernedLoopAdmissionContractHash.Matches(staleAuthorityProof));
        Assert.True(GovernedLoopAdmissionValidator.Validate(reboundAuthority).IsValid);
        Assert.NotEqual(authorityFailure.ContentHash, reboundAuthority.ContentHash);
        Assert.NotEqual(capabilityFailure.ContentHash, reboundCapability.ContentHash);
    }

    [Fact]
    public void Every_structured_proof_field_is_hash_bound_or_rejected()
    {
        var authority = GovernedLoopAdmissionTestFixture.Rejection(failureCode: GovernedLoopAdmissionFailureCode.AuthorityDenied);
        var authorityProof = authority.AuthorityDenial!;
        var changedBoundary = NewAuthorityProof(
            AuthorityBoundaryDecision.Deny,
            AuthorityBoundaryReason.InvalidContract,
            authority.RejectedAtUtc);
        var authorityVariations = new[]
        {
            new GovernedLoopAdmissionAuthorityDenialProof(
                authorityProof.SchemaVersion,
                WithMaxTargets(authorityProof.CandidateCeiling, authorityProof.CandidateCeiling.MaxTargetCount + 1),
                authorityProof.EffectiveCeiling,
                authorityProof.BoundaryReceipt),
            new GovernedLoopAdmissionAuthorityDenialProof(
                authorityProof.SchemaVersion,
                authorityProof.CandidateCeiling,
                authorityProof.EffectiveCeiling,
                changedBoundary.BoundaryReceipt)
        };

        Assert.All(
            authorityVariations,
            variation =>
            {
                var rebound = Rebind(authority, authorityDenial: variation);
                Assert.True(GovernedLoopAdmissionValidator.Validate(rebound).IsValid);
                Assert.NotEqual(authority.ContentHash, rebound.ContentHash);
            });

        var invalidAuthoritySchema = new GovernedLoopAdmissionAuthorityDenialProof(
            2,
            authorityProof.CandidateCeiling,
            authorityProof.EffectiveCeiling,
            authorityProof.BoundaryReceipt);
        Assert.Throws<ArgumentException>(() => Rebind(authority, authorityDenial: invalidAuthoritySchema));

        var capability = GovernedLoopAdmissionTestFixture.Rejection(failureCode: GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied);
        var capabilityProof = capability.CapabilityDenial!;
        Assert.True(CapabilityId.TryParse("org.embodysense/test/optional", out var optionalId, out _));
        Assert.True(CapabilityVersionRange.TryParse("[1.0.0]", out var optionalRange, out _));
        var changedRequirements = new CapabilityDependencyManifest(
            capabilityProof.Requirements.SchemaVersion,
            capabilityProof.Requirements.Kind,
            capabilityProof.Requirements.SubjectId,
            capabilityProof.Requirements.Required,
            [new CapabilityDependency(optionalId!, optionalRange!)],
            capabilityProof.Requirements.Artifact);
        var capabilityVariations = new[]
        {
            GovernedLoopAdmissionTestFixture.CapabilityDenialProof(
                changedRequirements,
                capabilityProof.EffectiveAuthority,
                capabilityProof.Violations,
                capabilityProof.EvaluatedAtUtc),
            GovernedLoopAdmissionTestFixture.CapabilityDenialProof(
                capabilityProof.Requirements,
                WithMaxTargets(capabilityProof.EffectiveAuthority, capabilityProof.EffectiveAuthority.MaxTargetCount + 1),
                capabilityProof.Violations,
                capabilityProof.EvaluatedAtUtc),
            GovernedLoopAdmissionTestFixture.CapabilityDenialProof(
                capabilityProof.Requirements,
                capabilityProof.EffectiveAuthority,
                capabilityProof.Violations,
                capabilityProof.EvaluatedAtUtc.AddTicks(1))
        };

        for (var index = 0; index < capabilityVariations.Length; index++)
        {
            var variation = capabilityVariations[index];
            var rebound = Rebind(
                capability,
                capabilityDenial: variation,
                rejectedAtUtc: index == 2 ? variation.EvaluatedAtUtc : null);
            Assert.True(GovernedLoopAdmissionValidator.Validate(rebound).IsValid);
            Assert.NotEqual(capability.ContentHash, rebound.ContentHash);
        }

        var invalidRequirementsHash = new GovernedLoopAdmissionCapabilityDenialProof(
            capabilityProof.SchemaVersion,
            capabilityProof.Requirements,
            DifferentHash(capabilityProof.RequirementsHash),
            capabilityProof.EffectiveAuthority,
            capabilityProof.Violations,
            capabilityProof.EvaluatedAtUtc);
        Assert.Throws<ArgumentException>(() => Rebind(capability, capabilityDenial: invalidRequirementsHash));
    }

    [Fact]
    public void Failure_classification_requires_its_exact_proof_composition()
    {
        var ordinary = GovernedLoopAdmissionTestFixture.Rejection();
        var authority = GovernedLoopAdmissionTestFixture.Rejection(failureCode: GovernedLoopAdmissionFailureCode.AuthorityDenied);
        var capability = GovernedLoopAdmissionTestFixture.Rejection(failureCode: GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied);
        var invalid = new[]
        {
            NewRejection(ordinary, authorityDenial: GovernedLoopAdmissionTestFixture.AuthorityDenialProof()),
            NewRejection(authority, omitAuthorityDenial: true),
            NewRejection(authority, capabilityDenial: GovernedLoopAdmissionTestFixture.CapabilityDenialProof()),
            NewRejection(capability, omitCapabilityDenial: true),
            NewRejection(capability, authorityDenial: GovernedLoopAdmissionTestFixture.AuthorityDenialProof())
        };

        Assert.All(invalid, candidate => Assert.False(GovernedLoopAdmissionValidator.Validate(candidate).IsValid));
    }

    [Fact]
    public void Authority_denial_requires_exact_deny_empty_effective_and_coherent_utc_time()
    {
        var valid = GovernedLoopAdmissionTestFixture.Rejection(failureCode: GovernedLoopAdmissionFailureCode.AuthorityDenied);
        var proof = valid.AuthorityDenial!;
        var direct = NewAuthorityProof(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary, valid.RejectedAtUtc);
        var paused = NewAuthorityProof(AuthorityBoundaryDecision.Pause, AuthorityBoundaryReason.ProfileSuspended, valid.RejectedAtUtc);
        var nonemptyEffective = new GovernedLoopAdmissionAuthorityDenialProof(
            proof.SchemaVersion,
            proof.CandidateCeiling,
            GovernedLoopAdmissionTestFixture.EffectiveAuthority(),
            proof.BoundaryReceipt);
        var wrongTime = NewAuthorityProof(AuthorityBoundaryDecision.Deny, AuthorityBoundaryReason.ProfileRetired, valid.RejectedAtUtc.AddTicks(1));
        var defaultTime = NewAuthorityProof(AuthorityBoundaryDecision.Deny, AuthorityBoundaryReason.ProfileRetired, default);

        Assert.All(
            new[] { direct, paused, nonemptyEffective, wrongTime, defaultTime },
            candidate => Assert.False(GovernedLoopAdmissionValidator.Validate(NewRejection(valid, authorityDenial: candidate)).IsValid));
    }

    [Fact]
    public void Capability_denial_requires_exact_nonempty_required_root_violations()
    {
        var valid = GovernedLoopAdmissionTestFixture.Rejection(failureCode: GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied);
        var proof = valid.CapabilityDenial!;
        var first = proof.Violations[0];
        Assert.True(CapabilityVersionRange.TryParse("[9.0.0]", out var wrongRange, out _));

        var optionalManifest = new CapabilityDependencyManifest(
            proof.Requirements.SchemaVersion,
            proof.Requirements.Kind,
            proof.Requirements.SubjectId,
            [],
            [new CapabilityDependency(first.DependencyId, first.CompatibleVersionRange)],
            proof.Requirements.Artifact);
        var matchingCapability = AuthorityGrantTestFixture.Capability(first.DependencyId.Value, "1.0.0");
        var compatibleAuthority = new AuthorityCeiling(
            [matchingCapability],
            [],
            0,
            CapabilitySideEffectClass.None,
            false,
            false,
            false);
        var candidates = new[]
        {
            GovernedLoopAdmissionTestFixture.CapabilityDenialProof(proof.Requirements, proof.EffectiveAuthority, []),
            GovernedLoopAdmissionTestFixture.CapabilityDenialProof(proof.Requirements, proof.EffectiveAuthority, [first, first]),
            GovernedLoopAdmissionTestFixture.CapabilityDenialProof(optionalManifest, proof.EffectiveAuthority, [first]),
            GovernedLoopAdmissionTestFixture.CapabilityDenialProof(proof.Requirements, compatibleAuthority, proof.Violations),
            GovernedLoopAdmissionTestFixture.CapabilityDenialProof(
                proof.Requirements,
                proof.EffectiveAuthority,
                [first with { CompatibleVersionRange = wrongRange! }, .. proof.Violations.Skip(1)]),
            GovernedLoopAdmissionTestFixture.CapabilityDenialProof(
                proof.Requirements,
                proof.EffectiveAuthority,
                [first with { Reason = GovernedLoopAdmissionCapabilityDenialReason.Unknown }, .. proof.Violations.Skip(1)])
        };

        Assert.All(candidates, candidate => Assert.False(GovernedLoopAdmissionValidator.Validate(NewRejection(valid, capabilityDenial: candidate)).IsValid));
        Assert.True(GovernedLoopAdmissionValidator.Validate(valid).IsValid);
    }

    [Fact]
    public void Denial_proofs_snapshot_bounded_caller_collections_and_expose_no_mutable_lists()
    {
        var authoritySource = GovernedLoopAdmissionTestFixture.AuthorityDenialProof();
        var capabilities = authoritySource.CandidateCeiling.Capabilities.ToList();
        var dataClasses = authoritySource.CandidateCeiling.DataClasses.ToList();
        var conditions = authoritySource.BoundaryReceipt.Conditions.ToList();
        var profiles = authoritySource.BoundaryReceipt.Profiles.ToList();
        Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(
            authoritySource.BoundaryReceipt.SchemaVersion,
            authoritySource.BoundaryReceipt.Decision,
            conditions,
            profiles,
            authoritySource.BoundaryReceipt.EvaluatedAtUtc,
            out var boundaryReceipt,
            out _));
        var candidateCeiling = new AuthorityCeiling(
            capabilities,
            dataClasses,
            authoritySource.CandidateCeiling.MaxTargetCount,
            authoritySource.CandidateCeiling.MaxSideEffectClass,
            authoritySource.CandidateCeiling.AllowsRecurrence,
            authoritySource.CandidateCeiling.AllowsExternalPublication,
            authoritySource.CandidateCeiling.AllowsIrreversibleAction);
        var authorityProof = new GovernedLoopAdmissionAuthorityDenialProof(
            authoritySource.SchemaVersion,
            candidateCeiling,
            authoritySource.EffectiveCeiling,
            boundaryReceipt!);

        capabilities.Clear();
        dataClasses.Clear();
        conditions.Clear();
        profiles.Clear();

        Assert.Equal(authoritySource.CandidateCeiling.DataClasses.Count, authorityProof.CandidateCeiling.DataClasses.Count);
        Assert.Equal(authoritySource.BoundaryReceipt.Conditions.Count, authorityProof.BoundaryReceipt.Conditions.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<CapabilityDataClass>)authorityProof.CandidateCeiling.DataClasses).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<AuthorityBoundaryCondition>)authorityProof.BoundaryReceipt.Conditions).Clear());

        var source = GovernedLoopAdmissionTestFixture.CapabilityDenialProof();
        var required = source.Requirements.Required.ToList();
        var optional = source.Requirements.Optional.ToList();
        var violations = source.Violations.ToList();
        var manifest = new CapabilityDependencyManifest(
            source.Requirements.SchemaVersion,
            source.Requirements.Kind,
            source.Requirements.SubjectId,
            required,
            optional,
            source.Requirements.Artifact);
        var proof = new GovernedLoopAdmissionCapabilityDenialProof(
            source.SchemaVersion,
            manifest,
            source.RequirementsHash,
            source.EffectiveAuthority,
            violations,
            source.EvaluatedAtUtc);

        required.Clear();
        optional.Add(source.Requirements.Required[0]);
        violations.Clear();

        Assert.Equal(source.Requirements.Required.Count, proof.Requirements.Required.Count);
        Assert.Empty(proof.Requirements.Optional);
        Assert.Equal(source.Violations.Count, proof.Violations.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<CapabilityDependency>)proof.Requirements.Required).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopAdmissionCapabilityDenialViolation>)proof.Violations).Clear());

        var oversized = new GovernedLoopAdmissionCapabilityDenialProof(
            source.SchemaVersion,
            source.Requirements,
            source.RequirementsHash,
            source.EffectiveAuthority,
            Enumerable.Repeat(source.Violations[0], GovernedLoopAdmissionLimits.MaxCapabilityDenialViolations + 100).ToArray(),
            source.EvaluatedAtUtc);
        Assert.Equal(GovernedLoopAdmissionLimits.MaxCapabilityDenialViolations + 1, oversized.Violations.Count);
        var valid = GovernedLoopAdmissionTestFixture.Rejection(failureCode: GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied);
        Assert.False(GovernedLoopAdmissionValidator.Validate(NewRejection(valid, capabilityDenial: oversized)).IsValid);

        var oversizedCapabilities = Enumerable.Range(0, AuthorityContractLimits.MaxCapabilitiesPerCeiling + 100)
            .Select(index => AuthorityGrantTestFixture.Capability($"org.embodysense/test/capability-{index}"))
            .ToArray();
        var oversizedCandidate = new AuthorityCeiling(
            oversizedCapabilities,
            authoritySource.CandidateCeiling.DataClasses,
            authoritySource.CandidateCeiling.MaxTargetCount,
            authoritySource.CandidateCeiling.MaxSideEffectClass,
            authoritySource.CandidateCeiling.AllowsRecurrence,
            authoritySource.CandidateCeiling.AllowsExternalPublication,
            authoritySource.CandidateCeiling.AllowsIrreversibleAction);
        var oversizedAuthority = new GovernedLoopAdmissionAuthorityDenialProof(
            authoritySource.SchemaVersion,
            oversizedCandidate,
            authoritySource.EffectiveCeiling,
            authoritySource.BoundaryReceipt);
        Assert.Equal(AuthorityContractLimits.MaxCapabilitiesPerCeiling + 1, oversizedAuthority.CandidateCeiling.Capabilities.Count);
        var validAuthority = GovernedLoopAdmissionTestFixture.Rejection(failureCode: GovernedLoopAdmissionFailureCode.AuthorityDenied);
        Assert.False(GovernedLoopAdmissionValidator.Validate(NewRejection(validAuthority, authorityDenial: oversizedAuthority)).IsValid);
    }

    [Fact]
    public void Serialized_denial_proofs_are_structured_bounded_and_diagnostic_free()
    {
        var capability = GovernedLoopAdmissionTestFixture.Rejection(failureCode: GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied);
        var authority = GovernedLoopAdmissionTestFixture.Rejection(failureCode: GovernedLoopAdmissionFailureCode.AuthorityDenied);
        var capabilityJson = JsonSerializer.Serialize(capability);
        var authorityJson = JsonSerializer.Serialize(authority);
        using var capabilityDocument = JsonDocument.Parse(capabilityJson);
        using var authorityDocument = JsonDocument.Parse(authorityJson);

        var capabilityProof = capabilityDocument.RootElement.GetProperty("CapabilityDenial");
        Assert.Equal(GovernedLoopAdmissionLimits.CurrentSchemaVersion, capabilityProof.GetProperty("SchemaVersion").GetInt32());
        Assert.True(capabilityProof.GetProperty("Violations").GetArrayLength() > 0);
        Assert.Equal(JsonValueKind.Null, capabilityDocument.RootElement.GetProperty("AuthorityDenial").ValueKind);
        Assert.Equal(
            (int)AuthorityBoundaryDecision.Deny,
            authorityDocument.RootElement.GetProperty("AuthorityDenial").GetProperty("BoundaryReceipt").GetProperty("Decision").GetInt32());
        Assert.DoesNotContain("detail", capabilityJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnostic", capabilityJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("message", capabilityJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretValue", capabilityJson, StringComparison.OrdinalIgnoreCase);
    }

    private static GovernedLoopAdmissionAuthorityDenialProof NewAuthorityProof(
        AuthorityBoundaryDecision decision,
        AuthorityBoundaryReason reason,
        DateTimeOffset evaluatedAtUtc)
    {
        IReadOnlyList<AuthorityProfileReference> profiles = decision == AuthorityBoundaryDecision.Deny
            ? []
            : [AuthorityGrantTestFixture.Grant().Binding.Profile.Reference];
        Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(
            AuthorityBoundaryReceipt.CurrentSchemaVersion,
            decision,
            [new AuthorityBoundaryCondition(decision, reason)],
            profiles,
            evaluatedAtUtc,
            out var receipt,
            out _));
        return new GovernedLoopAdmissionAuthorityDenialProof(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            GovernedLoopAdmissionTestFixture.EffectiveAuthority(),
            AuthorityCeilingIntersection.EmptyCeiling(),
            receipt!);
    }

    private static GovernedLoopAdmissionRejection Rebind(
        GovernedLoopAdmissionRejection value,
        GovernedLoopAdmissionAuthorityDenialProof? authorityDenial = null,
        GovernedLoopAdmissionCapabilityDenialProof? capabilityDenial = null,
        DateTimeOffset? rejectedAtUtc = null)
    {
        var authority = authorityDenial ?? value.AuthorityDenial;
        var capability = capabilityDenial ?? value.CapabilityDenial;
        var references = GovernedLoopAdmissionContractHash.CreateRejectionEvidenceReferences(value.Intent, value.FailureCode, authority, capability);
        return GovernedLoopAdmissionContractHash.Apply(NewRejection(
            value,
            authorityDenial: authority,
            capabilityDenial: capability,
            references: references,
            rejectedAtUtc: rejectedAtUtc));
    }

    private static GovernedLoopAdmissionRejection NewRejection(
        GovernedLoopAdmissionRejection value,
        GovernedLoopAdmissionIntent? intent = null,
        GovernedLoopAdmissionAuthorityDenialProof? authorityDenial = null,
        GovernedLoopAdmissionCapabilityDenialProof? capabilityDenial = null,
        IReadOnlyList<GovernedLoopAdmissionEvidenceReference>? references = null,
        bool omitAuthorityDenial = false,
        bool omitCapabilityDenial = false,
        DateTimeOffset? rejectedAtUtc = null)
        => new(
            value.SchemaVersion,
            intent ?? value.Intent,
            value.FailureCode,
            omitAuthorityDenial ? null : authorityDenial ?? value.AuthorityDenial,
            omitCapabilityDenial ? null : capabilityDenial ?? value.CapabilityDenial,
            references ?? value.References,
            rejectedAtUtc ?? value.RejectedAtUtc,
            string.Empty);

    private static AuthorityCeiling WithMaxTargets(AuthorityCeiling value, int maxTargetCount)
        => new(
            value.Capabilities,
            value.DataClasses,
            maxTargetCount,
            value.MaxSideEffectClass,
            value.AllowsRecurrence,
            value.AllowsExternalPublication,
            value.AllowsIrreversibleAction);

    private static string DifferentHash(string value)
        => value[0] == 'f' ? GovernedLoopAdmissionTestFixture.Hash('e') : GovernedLoopAdmissionTestFixture.Hash('f');
}
