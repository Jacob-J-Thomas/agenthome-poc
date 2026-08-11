using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Tests.Authority.Grants;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Authority;

public sealed class GovernedLoopEffectAuthorityContractTests
{
    [Fact]
    public void Exact_and_narrowed_direct_decisions_validate_through_the_public_boundary()
    {
        var exact = GovernedLoopEffectAuthorityTestFixture.Decision();
        var admitted = exact.AdmittedAuthority;
        var narrowedCeiling = GovernedLoopEffectAuthorityTestFixture.AdmittedCeiling(admitted.CapabilityPins[0], maxTargetCount: 1);
        var narrowed = GovernedLoopEffectAuthorityTestFixture.Decision(
            admitted,
            GovernedLoopEffectAuthorityTestFixture.Proof(
                grant: admitted.Grant,
                binding: admitted.Binding,
                boundary: admitted.Boundary,
                ceiling: narrowedCeiling,
                pins: admitted.CapabilityPins,
                dependencyEvidenceHash: admitted.DependencyEvidenceHash),
            reason: GovernedLoopEffectAuthorityReason.ActiveNarrowed);

        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(exact.AdmittedAuthority).IsValid);
        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(exact).IsValid);
        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(narrowed).IsValid);
    }

    [Fact]
    public void Unrelated_capability_drift_can_narrow_without_blocking_an_exact_required_pin()
    {
        var requiredPin = GovernedLoopEffectAuthorityTestFixture.Pin(0);
        var unrelatedPin = GovernedLoopEffectAuthorityTestFixture.Pin(1);
        var admittedCeiling = AuthorityGrantTestFixture.Ceiling(
            capabilities: [requiredPin.DescriptorIdentity, unrelatedPin.DescriptorIdentity],
            maxTargetCount: 2);
        var admitted = GovernedLoopEffectAuthorityTestFixture.Proof(
            ceiling: admittedCeiling,
            pins: [requiredPin, unrelatedPin]);
        var current = GovernedLoopEffectAuthorityTestFixture.CopyProof(
            admitted,
            pins: [requiredPin],
            observedPins: [unrelatedPin with { SafeDescription = unrelatedPin.SafeDescription + " Drift." }]);
        var required = GovernedLoopEffectAuthorityTestFixture.RequiredCeiling(requiredPin);
        var decision = GovernedLoopEffectAuthorityTestFixture.Decision(
            admitted,
            current,
            requiredAuthority: required,
            effectiveAuthority: required,
            requiredPins: [requiredPin],
            reason: GovernedLoopEffectAuthorityReason.ActiveNarrowed);

        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid);
    }

    [Fact]
    public void Current_observations_must_be_drifted_versions_of_admitted_capabilities()
    {
        var requiredPin = GovernedLoopEffectAuthorityTestFixture.Pin(0);
        var unrelatedPin = GovernedLoopEffectAuthorityTestFixture.Pin(1);
        var admittedCeiling = AuthorityGrantTestFixture.Ceiling(
            capabilities: [requiredPin.DescriptorIdentity, unrelatedPin.DescriptorIdentity],
            maxTargetCount: 2);
        var admitted = GovernedLoopEffectAuthorityTestFixture.Proof(
            ceiling: admittedCeiling,
            pins: [requiredPin, unrelatedPin]);
        var required = GovernedLoopEffectAuthorityTestFixture.RequiredCeiling(requiredPin);

        GovernedLoopEffectAuthorityDecision DecisionWithObservation(CapabilityAdmissionPin observation, bool applyHash = false)
        {
            var current = GovernedLoopEffectAuthorityTestFixture.CopyProof(
                admitted,
                pins: [requiredPin],
                observedPins: [observation]);
            var candidate = GovernedLoopEffectAuthorityTestFixture.Decision(
                admitted,
                current,
                requiredAuthority: required,
                effectiveAuthority: required,
                requiredPins: [requiredPin],
                reason: GovernedLoopEffectAuthorityReason.ActiveNarrowed,
                applyHash: false);
            return applyHash
                ? GovernedLoopEffectAuthorityContractHash.Apply(candidate)
                : candidate with { ContentHash = GovernedLoopEffectAuthorityTestFixture.Hash('f') };
        }

        var unchangedObservation = DecisionWithObservation(unrelatedPin);
        var unadmittedObservation = DecisionWithObservation(unrelatedPin with
        {
            DescriptorIdentity = AuthorityGrantTestFixture.Capability("org.embodysense/workspace/unadmitted")
        });
        var driftedObservation = DecisionWithObservation(
            unrelatedPin with { SafeDescription = unrelatedPin.SafeDescription + " Drift." },
            applyHash: true);

        Assert.Contains(
            GovernedLoopEffectAuthorityContractValidator.Validate(unchangedObservation).Errors,
            error => error.Path == "$.currentAuthority.observedCapabilityPins");
        Assert.Contains(
            GovernedLoopEffectAuthorityContractValidator.Validate(unadmittedObservation).Errors,
            error => error.Path == "$.currentAuthority.observedCapabilityPins");
        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(driftedObservation).IsValid);
    }

    [Fact]
    public void Null_unknown_schema_identity_bounds_hash_and_time_fail_closed()
    {
        var valid = GovernedLoopEffectAuthorityTestFixture.Decision();
        var candidates = new[]
        {
            valid with { SchemaVersion = 2 },
            valid with { RunId = "Run-1" },
            valid with { ExecutionGeneration = 0 },
            valid with { ExecutionGeneration = GovernedLoopEffectAuthorityContractLimits.MaxExecutionGeneration + 1 },
            valid with { NodeId = null! },
            valid with { NodeAttempt = 0 },
            valid with { EffectOperationId = new string('a', GovernedLoopEffectAuthorityContractLimits.MaxIdentifierCharacters + 1) },
            valid with { CorrelationId = "bad correlation" },
            valid with { BoundaryKind = (GovernedLoopEffectBoundaryKind)int.MaxValue },
            valid with { AdmissionReceiptHash = GovernedLoopEffectAuthorityTestFixture.Hash('A') },
            valid with { Disposition = (GovernedLoopEffectAuthorityDisposition)0 },
            valid with { Reason = (GovernedLoopEffectAuthorityReason)int.MaxValue },
            valid with { EvaluatedAtUtc = new DateTimeOffset(2026, 8, 10, 13, 1, 0, TimeSpan.FromHours(1)) },
            valid with { ContentHash = "sha256:" + GovernedLoopEffectAuthorityTestFixture.Hash('a') }
        };

        Assert.Contains(
            GovernedLoopEffectAuthorityContractValidator.Validate((GovernedLoopEffectAuthorityDecision?)null).Errors,
            error => error.Code == GovernedLoopEffectAuthorityValidationErrorCode.Required);
        Assert.All(candidates, candidate => Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(candidate).IsValid));
    }

    [Fact]
    public void Malformed_nested_proofs_and_capability_pins_fail_closed()
    {
        var valid = GovernedLoopEffectAuthorityTestFixture.Decision();
        var pin = valid.AdmittedAuthority.CapabilityPins[0];
        var malformedPin = pin with { Kind = (CapabilityKind)int.MaxValue };
        var invalidProofs = new[]
        {
            valid.AdmittedAuthority with { SchemaVersion = 2 },
            valid.AdmittedAuthority with { Grant = null! },
            GovernedLoopEffectAuthorityTestFixture.CopyProof(valid.AdmittedAuthority, omitBinding: true),
            valid.AdmittedAuthority with { GrantStatus = (AuthorityGrantLifecycleStatus)int.MaxValue },
            valid.AdmittedAuthority with { GrantPosture = (GovernedLoopEffectAuthorityGrantPosture)int.MaxValue },
            GovernedLoopEffectAuthorityTestFixture.CopyProof(valid.AdmittedAuthority, omitBoundary: true),
            GovernedLoopEffectAuthorityTestFixture.CopyProof(valid.AdmittedAuthority, omitCeiling: true),
            GovernedLoopEffectAuthorityTestFixture.CopyProof(valid.AdmittedAuthority, omitPins: true),
            GovernedLoopEffectAuthorityTestFixture.CopyProof(valid.AdmittedAuthority, pins: [malformedPin]),
            GovernedLoopEffectAuthorityTestFixture.CopyProof(valid.AdmittedAuthority, pins: [pin, pin]),
            GovernedLoopEffectAuthorityTestFixture.CopyProof(valid.AdmittedAuthority, omitObservedPins: true),
            valid.AdmittedAuthority with { DependencyEvidenceHash = GovernedLoopEffectAuthorityTestFixture.Hash('D') }
        };

        Assert.Contains(
            GovernedLoopEffectAuthorityContractValidator.Validate((GovernedLoopEffectAuthorityProof?)null).Errors,
            error => error.Code == GovernedLoopEffectAuthorityValidationErrorCode.Required);
        Assert.All(invalidProofs, proof => Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(proof).IsValid));
    }

    [Fact]
    public void Malformed_pin_details_out_of_ceiling_pins_and_overlapping_observations_fail_closed()
    {
        var proof = GovernedLoopEffectAuthorityTestFixture.Proof();
        var pin = proof.CapabilityPins[0];
        var malformedPins = new[]
        {
            pin with { Implementation = new CapabilityImplementationIdentity(pin.Implementation.ProviderId, "/bad") },
            pin with { Implementation = new CapabilityImplementationIdentity(pin.Implementation.ProviderId, "Bad-Path") },
            pin with { Provenance = new CapabilityProvenance(pin.Provenance.Kind, "http://example.test/source", pin.Provenance.SourceRevision, pin.Provenance.Integrity) },
            pin with { Provenance = pin.Provenance with { SourceRevision = "bad revision" } },
            pin with { Artifact = new CapabilityDependencyArtifactMetadata(null, "bad signature") },
            pin with { SafeDescription = "bad\0description" }
        };
        var outsideCeiling = GovernedLoopEffectAuthorityTestFixture.CopyProof(
            proof,
            ceiling: AuthorityCeilingIntersection.EmptyCeiling());
        var overlappingObservation = GovernedLoopEffectAuthorityTestFixture.CopyProof(
            proof,
            observedPins: [pin with { SafeDescription = pin.SafeDescription + " Drift." }]);
        var invalidGrant = proof with
        {
            Grant = new AuthorityGrantReference(
                proof.Grant.GrantId,
                proof.Grant.Revision,
                "sha256:" + GovernedLoopEffectAuthorityTestFixture.Hash('A'))
        };

        Assert.All(
            malformedPins,
            candidate => Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(
                GovernedLoopEffectAuthorityTestFixture.CopyProof(proof, pins: [candidate])).IsValid));
        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(outsideCeiling).IsValid);
        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(overlappingObservation).IsValid);
        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(invalidGrant).IsValid);
    }

    [Fact]
    public void Direct_requires_exact_current_binding_active_time_monotonic_authority_and_required_pins()
    {
        var valid = GovernedLoopEffectAuthorityTestFixture.Decision();
        var widened = GovernedLoopEffectAuthorityTestFixture.AdmittedCeiling(valid.RequiredCapabilityPins[0], maxTargetCount: 3);
        var wrongDependency = valid.CurrentAuthority! with { DependencyEvidenceHash = GovernedLoopEffectAuthorityTestFixture.Hash('e') };
        var missingPin = GovernedLoopEffectAuthorityTestFixture.CopyProof(valid.CurrentAuthority!, pins: []);
        var inactiveCurrent = valid.CurrentAuthority! with
        {
            GrantStatus = AuthorityGrantLifecycleStatus.Suspended,
            GrantPosture = GovernedLoopEffectAuthorityGrantPosture.Suspended
        };
        var admittedWithoutPins = GovernedLoopEffectAuthorityTestFixture.CopyProof(valid.AdmittedAuthority, pins: []);
        var admittedWithoutDependencyHash = valid.AdmittedAuthority with { DependencyEvidenceHash = null };
        var emptyRequired = AuthorityCeilingIntersection.EmptyCeiling();
        var invalid = new[]
        {
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(valid, omitCurrent: true),
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(valid, current: valid.CurrentAuthority! with { GrantStatus = AuthorityGrantLifecycleStatus.Suspended }),
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(valid, current: inactiveCurrent),
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(valid, current: wrongDependency),
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(valid, current: GovernedLoopEffectAuthorityTestFixture.CopyProof(valid.CurrentAuthority!, ceiling: widened)),
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(valid, current: missingPin),
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(valid, effectiveAuthority: valid.AdmittedAuthority.Ceiling),
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(valid, requiredAuthority: widened),
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(valid, requiredPins: []),
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(valid, admitted: admittedWithoutPins),
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(valid, admitted: admittedWithoutDependencyHash),
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(valid, requiredAuthority: emptyRequired, effectiveAuthority: emptyRequired),
            valid with { EvaluatedAtUtc = valid.AdmittedAuthority.Boundary.EffectiveAtUtc.AddTicks(-1) },
            valid with { Reason = GovernedLoopEffectAuthorityReason.ActiveNarrowed }
        };

        Assert.All(invalid, candidate => Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(candidate).IsValid));
    }

    [Theory]
    [InlineData(GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.GrantUnavailable)]
    [InlineData(GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.GrantAmbiguous)]
    [InlineData(GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantMissing)]
    [InlineData(GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantInvalid)]
    [InlineData(GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.InvalidRequest)]
    public void Unresolved_current_proof_accepts_only_its_closed_pause_or_deny_reason(
        GovernedLoopEffectAuthorityDisposition disposition,
        GovernedLoopEffectAuthorityReason reason)
    {
        var valid = GovernedLoopEffectAuthorityTestFixture.Decision(
            omitCurrent: true,
            disposition: disposition,
            reason: reason);
        var wrongDisposition = valid with
        {
            Disposition = disposition == GovernedLoopEffectAuthorityDisposition.Pause
                ? GovernedLoopEffectAuthorityDisposition.Deny
                : GovernedLoopEffectAuthorityDisposition.Pause
        };

        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(valid).IsValid);
        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(wrongDisposition).IsValid);
    }

    [Theory]
    [InlineData(GovernedLoopEffectAuthorityReason.CapabilityUnavailable)]
    [InlineData(GovernedLoopEffectAuthorityReason.CapabilityAmbiguous)]
    [InlineData(GovernedLoopEffectAuthorityReason.EvidenceUnavailable)]
    [InlineData(GovernedLoopEffectAuthorityReason.EvidenceAmbiguous)]
    [InlineData(GovernedLoopEffectAuthorityReason.EvidenceConflict)]
    public void Conclusive_grant_proof_can_pause_only_for_capability_or_evidence_uncertainty(GovernedLoopEffectAuthorityReason reason)
    {
        var valid = GovernedLoopEffectAuthorityTestFixture.Decision(
            disposition: GovernedLoopEffectAuthorityDisposition.Pause,
            reason: reason);

        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(valid).IsValid);
        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(valid with { Reason = GovernedLoopEffectAuthorityReason.GrantUnavailable }).IsValid);
    }

    [Fact]
    public void Capability_and_evidence_pauses_cannot_hide_a_required_drift_or_an_uncommittable_direct_candidate()
    {
        var direct = GovernedLoopEffectAuthorityTestFixture.Decision();
        var requiredPin = direct.RequiredCapabilityPins[0];
        var wrongDependency = direct.CurrentAuthority! with { DependencyEvidenceHash = GovernedLoopEffectAuthorityTestFixture.Hash('e') };
        var missingRequired = GovernedLoopEffectAuthorityTestFixture.CopyProof(direct.CurrentAuthority!, pins: []);
        var observedRequired = GovernedLoopEffectAuthorityTestFixture.CopyProof(
            direct.CurrentAuthority!,
            pins: [],
            observedPins: [requiredPin with { SafeDescription = requiredPin.SafeDescription + " Drift." }]);
        var evidencePause = GovernedLoopEffectAuthorityTestFixture.Decision(
            disposition: GovernedLoopEffectAuthorityDisposition.Pause,
            reason: GovernedLoopEffectAuthorityReason.EvidenceUnavailable);
        var capabilityPause = GovernedLoopEffectAuthorityTestFixture.Decision(
            disposition: GovernedLoopEffectAuthorityDisposition.Pause,
            reason: GovernedLoopEffectAuthorityReason.CapabilityUnavailable);

        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(evidencePause, current: wrongDependency)).IsValid);
        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(evidencePause, current: missingRequired)).IsValid);
        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(capabilityPause, current: observedRequired)).IsValid);
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectAuthorityContractHash.Apply(
            GovernedLoopEffectAuthorityTestFixture.Decision(
                disposition: GovernedLoopEffectAuthorityDisposition.Deny,
                reason: GovernedLoopEffectAuthorityReason.ActiveExact,
                applyHash: false)));
    }

    [Theory]
    [InlineData(AuthorityGrantLifecycleStatus.Suspended, GovernedLoopEffectAuthorityReason.GrantSuspended)]
    [InlineData(AuthorityGrantLifecycleStatus.Revoked, GovernedLoopEffectAuthorityReason.GrantRevoked)]
    [InlineData(AuthorityGrantLifecycleStatus.Expired, GovernedLoopEffectAuthorityReason.GrantExpired)]
    public void Conclusive_inactive_grant_status_has_one_exact_deny_reason(
        AuthorityGrantLifecycleStatus status,
        GovernedLoopEffectAuthorityReason reason)
    {
        var admitted = GovernedLoopEffectAuthorityTestFixture.Proof();
        var posture = status switch
        {
            AuthorityGrantLifecycleStatus.Suspended => GovernedLoopEffectAuthorityGrantPosture.Suspended,
            AuthorityGrantLifecycleStatus.Revoked => GovernedLoopEffectAuthorityGrantPosture.Revoked,
            AuthorityGrantLifecycleStatus.Expired => GovernedLoopEffectAuthorityGrantPosture.Expired,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
        var current = admitted with { GrantStatus = status, GrantPosture = posture };
        var valid = GovernedLoopEffectAuthorityTestFixture.Decision(
            admitted,
            current,
            disposition: GovernedLoopEffectAuthorityDisposition.Deny,
            reason: reason);

        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(valid).IsValid);
        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(valid with { Reason = GovernedLoopEffectAuthorityReason.BindingMismatch }).IsValid);
    }

    [Fact]
    public void Durable_first_bound_run_completion_has_one_exact_nonrenewable_deny_posture()
    {
        var boundary = AuthorityGrantTestFixture.Boundary(
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);
        var admitted = GovernedLoopEffectAuthorityTestFixture.Proof(boundary: boundary);
        var current = GovernedLoopEffectAuthorityTestFixture.Proof(
            grantPosture: GovernedLoopEffectAuthorityGrantPosture.Completed,
            grant: admitted.Grant,
            binding: admitted.Binding,
            boundary: boundary,
            ceiling: admitted.Ceiling,
            pins: admitted.CapabilityPins,
            omitDependencyEvidenceHash: true);
        var valid = GovernedLoopEffectAuthorityTestFixture.Decision(
            admitted,
            current,
            disposition: GovernedLoopEffectAuthorityDisposition.Deny,
            reason: GovernedLoopEffectAuthorityReason.GrantCompleted);

        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(valid).IsValid);
        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(valid with { Reason = GovernedLoopEffectAuthorityReason.GrantExpired }).IsValid);
        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(
                valid,
                current: GovernedLoopEffectAuthorityTestFixture.CopyProof(
                    current,
                    boundary: AuthorityGrantTestFixture.Boundary(completionConstraint: AuthorityGrantCompletionConstraintKind.None)))).IsValid);
    }

    [Theory]
    [InlineData(GovernedLoopEffectAuthorityGrantPosture.ProfileUnavailable, GovernedLoopEffectAuthorityReason.ProfileUnavailable)]
    [InlineData(GovernedLoopEffectAuthorityGrantPosture.RoleUnavailable, GovernedLoopEffectAuthorityReason.RoleUnavailable)]
    [InlineData(GovernedLoopEffectAuthorityGrantPosture.LoopUnavailable, GovernedLoopEffectAuthorityReason.LoopUnavailable)]
    [InlineData(GovernedLoopEffectAuthorityGrantPosture.CeilingExceeded, GovernedLoopEffectAuthorityReason.CeilingExceeded)]
    public void Bound_dependency_resolution_postures_have_one_exact_deny_reason_without_fabricated_hashes(
        GovernedLoopEffectAuthorityGrantPosture posture,
        GovernedLoopEffectAuthorityReason reason)
    {
        var admitted = GovernedLoopEffectAuthorityTestFixture.Proof();
        var current = GovernedLoopEffectAuthorityTestFixture.Proof(
            grantPosture: posture,
            grant: admitted.Grant,
            binding: admitted.Binding,
            boundary: admitted.Boundary,
            ceiling: admitted.Ceiling,
            pins: admitted.CapabilityPins,
            omitDependencyEvidenceHash: true);
        var decision = GovernedLoopEffectAuthorityTestFixture.Decision(
            admitted,
            current,
            disposition: GovernedLoopEffectAuthorityDisposition.Deny,
            reason: reason);

        Assert.Null(decision.CurrentAuthority!.DependencyEvidenceHash);
        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid);
        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(decision with { Reason = GovernedLoopEffectAuthorityReason.DependencyMismatch }).IsValid);
    }

    [Fact]
    public void Grant_posture_must_match_lifecycle_and_retained_time_or_stale_evidence()
    {
        var futureBoundary = new AuthorityGrantBoundary(
                GovernedLoopEffectAuthorityTestFixture.EvaluatedAtUtc.AddMinutes(1),
                GovernedLoopEffectAuthorityTestFixture.EvaluatedAtUtc.AddHours(1),
                AuthorityGrantCompletionConstraintKind.None);
        var admitted = GovernedLoopEffectAuthorityTestFixture.Proof(boundary: futureBoundary);
        var future = admitted with
        {
            GrantPosture = GovernedLoopEffectAuthorityGrantPosture.NotEffective
        };
        var valid = GovernedLoopEffectAuthorityTestFixture.Decision(
            admitted,
            future,
            disposition: GovernedLoopEffectAuthorityDisposition.Deny,
            reason: GovernedLoopEffectAuthorityReason.GrantNotEffective);
        var activeAdmitted = GovernedLoopEffectAuthorityTestFixture.Proof();
        var falseNotEffective = GovernedLoopEffectAuthorityTestFixture.Decision(
            activeAdmitted,
            activeAdmitted with { GrantPosture = GovernedLoopEffectAuthorityGrantPosture.NotEffective },
            disposition: GovernedLoopEffectAuthorityDisposition.Deny,
            reason: GovernedLoopEffectAuthorityReason.GrantNotEffective,
            applyHash: false);
        var falseStale = GovernedLoopEffectAuthorityTestFixture.Decision(
            activeAdmitted,
            activeAdmitted with { GrantPosture = GovernedLoopEffectAuthorityGrantPosture.Stale },
            disposition: GovernedLoopEffectAuthorityDisposition.Deny,
            reason: GovernedLoopEffectAuthorityReason.GrantStale,
            applyHash: false);

        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(valid).IsValid);
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectAuthorityContractHash.Apply(falseNotEffective));
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectAuthorityContractHash.Apply(falseStale));
        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(
            admitted with { GrantStatus = AuthorityGrantLifecycleStatus.Suspended }).IsValid);
    }

    [Fact]
    public void Current_drift_missing_pin_and_noncapability_narrowing_have_distinct_deny_reasons()
    {
        var admitted = GovernedLoopEffectAuthorityTestFixture.Proof();
        var pin = admitted.CapabilityPins[0];
        var driftedPin = pin with { SafeDescription = pin.SafeDescription + " Changed." };
        var drifted = GovernedLoopEffectAuthorityTestFixture.CopyProof(admitted, pins: [], observedPins: [driftedPin]);
        var inactive = GovernedLoopEffectAuthorityTestFixture.CopyProof(admitted, pins: []);
        var outside = GovernedLoopEffectAuthorityTestFixture.CopyProof(
            admitted,
            ceiling: new AuthorityCeiling(
                admitted.Ceiling.Capabilities,
                admitted.Ceiling.DataClasses,
                0,
                admitted.Ceiling.MaxSideEffectClass,
                admitted.Ceiling.AllowsRecurrence,
                admitted.Ceiling.AllowsExternalPublication,
                admitted.Ceiling.AllowsIrreversibleAction));
        var cases = new[]
        {
            (drifted, GovernedLoopEffectAuthorityReason.CapabilityDrifted),
            (inactive, GovernedLoopEffectAuthorityReason.CapabilityInactive),
            (outside, GovernedLoopEffectAuthorityReason.EffectOutsideCeiling)
        };

        foreach (var (current, reason) in cases)
        {
            var decision = GovernedLoopEffectAuthorityTestFixture.Decision(
                admitted,
                current,
                disposition: GovernedLoopEffectAuthorityDisposition.Deny,
                reason: reason);
            Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid, reason.ToString());
        }
    }

    [Fact]
    public void Stale_binding_dependency_and_trusted_time_denials_are_not_interchangeable()
    {
        var admitted = GovernedLoopEffectAuthorityTestFixture.Proof();
        var successor = AuthorityGrantTestFixture.Successor(AuthorityGrantTestFixture.Grant(binding: admitted.Binding, ceiling: admitted.Ceiling, boundary: admitted.Boundary));
        var stale = admitted with
        {
            Grant = new AuthorityGrantReference(successor.GrantId, successor.Revision, successor.ContentHash),
            GrantPosture = GovernedLoopEffectAuthorityGrantPosture.Stale
        };
        var activeStale = stale with { GrantPosture = GovernedLoopEffectAuthorityGrantPosture.Active };
        var binding = GovernedLoopEffectAuthorityTestFixture.CopyProof(admitted, binding: AuthorityGrantTestFixture.Binding(roleRevision: 99));
        var dependency = admitted with { DependencyEvidenceHash = GovernedLoopEffectAuthorityTestFixture.Hash('e') };
        var notEffectiveBoundary = new AuthorityGrantBoundary(
            GovernedLoopEffectAuthorityTestFixture.EvaluatedAtUtc.AddMinutes(1),
            GovernedLoopEffectAuthorityTestFixture.EvaluatedAtUtc.AddHours(1),
            admitted.Boundary.CompletionConstraint);
        var notEffective = GovernedLoopEffectAuthorityTestFixture.CopyProof(admitted, boundary: notEffectiveBoundary) with
        {
            GrantPosture = GovernedLoopEffectAuthorityGrantPosture.NotEffective
        };
        var cases = new[]
        {
            (stale, GovernedLoopEffectAuthorityReason.GrantStale),
            (binding, GovernedLoopEffectAuthorityReason.BindingMismatch),
            (dependency, GovernedLoopEffectAuthorityReason.DependencyMismatch),
            (notEffective, GovernedLoopEffectAuthorityReason.GrantNotEffective)
        };

        foreach (var (current, reason) in cases)
        {
            var decision = GovernedLoopEffectAuthorityTestFixture.Decision(
                admitted,
                current,
                disposition: GovernedLoopEffectAuthorityDisposition.Deny,
                reason: reason);
            Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid, reason.ToString());
            Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(decision with { Reason = GovernedLoopEffectAuthorityReason.GrantRevoked }).IsValid);
        }

        Assert.Throws<ArgumentException>(() => GovernedLoopEffectAuthorityContractHash.Apply(
            GovernedLoopEffectAuthorityTestFixture.Decision(
                admitted,
                activeStale,
                disposition: GovernedLoopEffectAuthorityDisposition.Deny,
                reason: GovernedLoopEffectAuthorityReason.GrantStale,
                applyHash: false)));
    }

    [Fact]
    public void Pause_and_deny_require_empty_effective_authority_but_retain_required_authority()
    {
        var pause = GovernedLoopEffectAuthorityTestFixture.Decision(
            omitCurrent: true,
            disposition: GovernedLoopEffectAuthorityDisposition.Pause,
            reason: GovernedLoopEffectAuthorityReason.GrantUnavailable);
        var deny = GovernedLoopEffectAuthorityTestFixture.Decision(
            omitCurrent: true,
            disposition: GovernedLoopEffectAuthorityDisposition.Deny,
            reason: GovernedLoopEffectAuthorityReason.GrantMissing);

        Assert.NotEmpty(pause.RequiredAuthority.Capabilities);
        Assert.Empty(pause.EffectiveAuthority.Capabilities);
        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(pause).IsValid);
        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(deny).IsValid);
        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(pause, effectiveAuthority: pause.RequiredAuthority)).IsValid);
        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(
            GovernedLoopEffectAuthorityTestFixture.CopyDecision(deny, effectiveAuthority: deny.RequiredAuthority)).IsValid);
    }
}
