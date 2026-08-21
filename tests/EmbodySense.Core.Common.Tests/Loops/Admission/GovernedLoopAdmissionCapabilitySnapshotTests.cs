using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Admission;

public sealed class GovernedLoopAdmissionCapabilitySnapshotTests
{
    [Fact]
    public void Empty_capability_proof_is_valid_only_when_no_selected_evidence_exists()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var populated = GovernedLoopAdmissionTestFixture.CapabilityAdmission();
        var emptyManifest = populated.Requirements with { Required = [], Optional = [] };
        Assert.True(CapabilityDependencyManifestHash.TryCompute(emptyManifest, out var manifestHash, out _));
        var empty = new CapabilityAdmissionSnapshot(
            CapabilityAdmissionSnapshot.CurrentSchemaVersion,
            intent.WorkspaceId,
            emptyManifest,
            manifestHash!.Value,
            [],
            [],
            GovernedLoopAdmissionTestFixture.CapabilityAdmittedAtUtc);
        var inconsistent = empty with { Evidence = [populated.Evidence[0]] };
        var requiredWithoutProof = populated with { Pins = [], Evidence = [] };

        Assert.Null(CapabilityAdmissionSnapshotValidator.Validate(empty));
        Assert.NotNull(CapabilityAdmissionSnapshotValidator.Validate(inconsistent));
        Assert.NotNull(CapabilityAdmissionSnapshotValidator.Validate(requiredWithoutProof));

        var populatedAuthority = GovernedLoopAdmissionTestFixture.EffectiveAuthority();
        var authority = new AuthorityCeiling(
            [],
            populatedAuthority.DataClasses,
            populatedAuthority.MaxTargetCount,
            populatedAuthority.MaxSideEffectClass,
            populatedAuthority.AllowsRecurrence,
            populatedAuthority.AllowsExternalPublication,
            populatedAuthority.AllowsIrreversibleAction);
        var evidence = GovernedLoopAdmissionTestFixture.Evidence(
            intent,
            effectiveAuthority: authority,
            capabilityAdmission: empty);

        Assert.True(GovernedLoopAdmissionValidator.Validate(evidence, intent).IsValid);
        Assert.True(GovernedLoopAdmissionContractHash.Matches(evidence));
        Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(inconsistent));
    }

    [Fact]
    public void Optional_only_omission_is_a_valid_zero_pin_resolution_proof()
    {
        var populated = GovernedLoopAdmissionTestFixture.CapabilityAdmission();
        var optionalDependency = populated.Requirements.Required[0];
        var optionalManifest = populated.Requirements with
        {
            Required = [],
            Optional = [optionalDependency]
        };
        Assert.True(CapabilityDependencyManifestHash.TryCompute(optionalManifest, out var manifestHash, out _));
        var omission = populated.Evidence[0] with
        {
            SubjectId = optionalManifest.SubjectId,
            DependencyId = optionalDependency.CapabilityId,
            CompatibleVersionRange = optionalDependency.CompatibleVersionRange,
            IsOptional = true,
            Outcome = "OmittedOptional",
            SelectedIdentity = null,
            Detail = "The optional capability was omitted without granting authority."
        };
        var snapshot = new CapabilityAdmissionSnapshot(
            CapabilityAdmissionSnapshot.CurrentSchemaVersion,
            GovernedLoopAdmissionTestFixture.WorkspaceId,
            optionalManifest,
            manifestHash!.Value,
            [],
            [omission],
            GovernedLoopAdmissionTestFixture.CapabilityAdmittedAtUtc);

        Assert.Null(CapabilityAdmissionSnapshotValidator.Validate(snapshot));
        Assert.Equal(
            GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(snapshot),
            GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(snapshot with { }));
    }

    [Fact]
    public void Hostile_nested_capability_shapes_fail_closed_without_escaping_public_validation()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var validEvidence = GovernedLoopAdmissionTestFixture.Evidence(intent);
        var snapshot = validEvidence.CapabilityAdmission;
        var pin = snapshot.Pins[0];
        var identity = pin.DescriptorIdentity;
        var selected = snapshot.Evidence[0];

        CapabilityAdmissionSnapshot[] malformed =
        [
            snapshot with { WorkspaceScopeId = "workspace" },
            WithPin(snapshot, pin with
            {
                DescriptorIdentity = new CapabilityDescriptorIdentity(null!, identity.Version, identity.Hash)
            }),
            WithPin(snapshot, pin with { Kind = (CapabilityKind)int.MaxValue }),
            WithPin(snapshot, pin with
            {
                Implementation = new CapabilityImplementationIdentity(null!, pin.Implementation.ImplementationId)
            }),
            WithPin(snapshot, pin with
            {
                Implementation = pin.Implementation with
                {
                    ImplementationId = new string('a', CapabilityContractLimits.MaxImplementationIdCharacters + 1)
                }
            }),
            WithPin(snapshot, pin with
            {
                Provenance = pin.Provenance with { SourceUri = "https://user:password@example.com/capability" }
            }),
            WithPin(snapshot, pin with
            {
                Provenance = pin.Provenance with { Kind = (CapabilityProvenanceKind)int.MaxValue }
            }),
            WithPin(snapshot, pin with
            {
                Artifact = pin.Artifact with { Signature = "credential bearing signature" }
            }),
            WithPin(snapshot, pin with
            {
                SafeDescription = new string('x', CapabilityContractLimits.MaxPurposeCharacters + 1)
            }),
            WithEvidence(snapshot, selected with
            {
                SelectedIdentity = new CapabilityDescriptorIdentity(null!, identity.Version, identity.Hash)
            })
        ];

        Assert.All(malformed, candidate => AssertMalformed(candidate, validEvidence, intent));
    }

    [Fact]
    public void Canonical_capability_reference_hash_is_independent_of_set_like_collection_order()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var evidence = GovernedLoopAdmissionTestFixture.Evidence(intent);
        var snapshot = evidence.CapabilityAdmission;
        var reordered = snapshot with
        {
            Pins = snapshot.Pins.Reverse().ToArray(),
            Evidence = snapshot.Evidence.Reverse().ToArray()
        };

        Assert.True(snapshot.Pins.Count > 1);
        Assert.True(snapshot.Evidence.Count > 1);
        Assert.Equal(
            GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(snapshot),
            GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(reordered));

        var reorderedReferences = GovernedLoopAdmissionContractHash.CreateEvidenceReferences(
            intent,
            evidence.EffectiveAuthority,
            reordered,
            evidence.ModelRoutingAdmission);
        var reorderedEvidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            evidence.SchemaVersion,
            evidence.IntentHash,
            evidence.Binding,
            evidence.GrantProfile,
            evidence.GrantBoundary,
            evidence.GrantDependencyEvidenceHash,
            evidence.EffectiveAuthority,
            reordered,
            evidence.ModelRoutingAdmission,
            reorderedReferences,
            evidence.EvaluatedAtUtc,
            string.Empty));

        Assert.Equal(evidence.References, reorderedReferences);
        Assert.Equal(evidence.ContentHash, reorderedEvidence.ContentHash);
        Assert.True(GovernedLoopAdmissionValidator.Validate(reorderedEvidence, intent).IsValid);
    }

    private static void AssertMalformed(
        CapabilityAdmissionSnapshot capabilityAdmission,
        GovernedLoopAdmissionEvidence validEvidence,
        GovernedLoopAdmissionIntent intent)
    {
        var candidate = new GovernedLoopAdmissionEvidence(
            validEvidence.SchemaVersion,
            validEvidence.IntentHash,
            validEvidence.Binding,
            validEvidence.GrantProfile,
            validEvidence.GrantBoundary,
            validEvidence.GrantDependencyEvidenceHash,
            validEvidence.EffectiveAuthority,
            capabilityAdmission,
            validEvidence.ModelRoutingAdmission,
            validEvidence.References,
            validEvidence.EvaluatedAtUtc,
            validEvidence.ContentHash);

        var exception = Record.Exception(() => GovernedLoopAdmissionValidator.Validate(candidate, intent));

        Assert.Null(exception);
        Assert.False(GovernedLoopAdmissionValidator.Validate(candidate, intent).IsValid);
        Assert.False(GovernedLoopAdmissionContractHash.Matches(candidate));
        Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(capabilityAdmission));
    }

    private static CapabilityAdmissionSnapshot WithPin(CapabilityAdmissionSnapshot snapshot, CapabilityAdmissionPin replacement)
        => snapshot with { Pins = [replacement, .. snapshot.Pins.Skip(1)] };

    private static CapabilityAdmissionSnapshot WithEvidence(CapabilityAdmissionSnapshot snapshot, CapabilityAdmissionEvidence replacement)
        => snapshot with { Evidence = [replacement, .. snapshot.Evidence.Skip(1)] };
}
