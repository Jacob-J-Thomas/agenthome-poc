using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Common.Tests.Loops.Admission;

public sealed class GovernedLoopAdmissionDefensiveCopyTests
{
    [Fact]
    public void Successful_evidence_deep_snapshots_all_caller_owned_nested_collections()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var authority = GovernedLoopAdmissionTestFixture.EffectiveAuthority();
        var capabilities = GovernedLoopAdmissionTestFixture.CapabilityAdmission();
        var required = capabilities.Requirements.Required.ToList();
        var optional = capabilities.Requirements.Optional.ToList();
        var pins = capabilities.Pins.ToList();
        var capabilityEvidence = capabilities.Evidence.ToList();
        var requirements = new CapabilityDependencyManifest(
            capabilities.Requirements.SchemaVersion,
            capabilities.Requirements.Kind,
            capabilities.Requirements.SubjectId,
            required,
            optional,
            capabilities.Requirements.Artifact);
        var callerOwnedCapabilities = new CapabilityAdmissionSnapshot(
            capabilities.SchemaVersion,
            capabilities.WorkspaceScopeId,
            requirements,
            capabilities.RequirementsHash,
            pins,
            capabilityEvidence,
            capabilities.AdmittedAtUtc);
        var references = GovernedLoopAdmissionContractHash
            .CreateEvidenceReferences(intent, authority, callerOwnedCapabilities)
            .ToList();
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            GovernedLoopExecutionBinding.Create(1, "run-1", intent.Publication.Revision, 1),
            GovernedLoopAdmissionTestFixture.Evidence(intent).GrantProfile,
            GovernedLoopAdmissionTestFixture.Evidence(intent).GrantBoundary,
            GovernedLoopAdmissionTestFixture.Hash('9'),
            authority,
            callerOwnedCapabilities,
            references,
            GovernedLoopAdmissionTestFixture.EvaluatedAtUtc,
            string.Empty));
        var expectedHash = evidence.ContentHash;
        var expectedRequiredCount = required.Count;
        var expectedPinCount = pins.Count;
        var expectedCapabilityEvidenceCount = capabilityEvidence.Count;
        var expectedReferenceCount = references.Count;

        required.Clear();
        optional.Add(capabilities.Requirements.Required[0]);
        pins.Clear();
        capabilityEvidence.Clear();
        references.Clear();

        Assert.Equal(expectedRequiredCount, evidence.CapabilityAdmission.Requirements.Required.Count);
        Assert.Empty(evidence.CapabilityAdmission.Requirements.Optional);
        Assert.Equal(expectedPinCount, evidence.CapabilityAdmission.Pins.Count);
        Assert.Equal(expectedCapabilityEvidenceCount, evidence.CapabilityAdmission.Evidence.Count);
        Assert.Equal(expectedReferenceCount, evidence.References.Count);
        Assert.Equal(expectedHash, evidence.ContentHash);
        Assert.True(GovernedLoopAdmissionContractHash.Matches(evidence));
        Assert.True(GovernedLoopAdmissionValidator.Validate(evidence, intent).IsValid);
    }

    [Fact]
    public void Rejection_snapshots_caller_owned_references()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var references = new List<GovernedLoopAdmissionEvidenceReference>
        {
            GovernedLoopAdmissionTestFixture.RoleReference(intent)
        };
        var rejection = GovernedLoopAdmissionTestFixture.Rejection(intent, references: references);
        var expectedHash = rejection.ContentHash;

        references.Clear();

        Assert.Single(rejection.References);
        Assert.Equal(expectedHash, rejection.ContentHash);
        Assert.True(GovernedLoopAdmissionContractHash.Matches(rejection));
        Assert.True(GovernedLoopAdmissionValidator.Validate(rejection).IsValid);
    }

    [Fact]
    public void Every_exposed_collection_snapshot_is_read_only()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var evidence = GovernedLoopAdmissionTestFixture.Evidence(intent);
        var rejection = GovernedLoopAdmissionTestFixture.Rejection(intent);

        AssertReadOnly(evidence.EffectiveAuthority.Capabilities);
        AssertReadOnly(evidence.EffectiveAuthority.DataClasses);
        AssertReadOnly(evidence.CapabilityAdmission.Requirements.Required);
        AssertReadOnly(evidence.CapabilityAdmission.Requirements.Optional);
        AssertReadOnly(evidence.CapabilityAdmission.Pins);
        AssertReadOnly(evidence.CapabilityAdmission.Evidence);
        AssertReadOnly(evidence.References);
        AssertReadOnly(rejection.References);
    }

    [Fact]
    public void Malformed_null_collections_are_preserved_for_fail_closed_validation_instead_of_sanitized()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var validEvidence = GovernedLoopAdmissionTestFixture.Evidence(intent);
        var malformedAuthority = new AuthorityCeiling(
            null!,
            validEvidence.EffectiveAuthority.DataClasses,
            validEvidence.EffectiveAuthority.MaxTargetCount,
            validEvidence.EffectiveAuthority.MaxSideEffectClass,
            validEvidence.EffectiveAuthority.AllowsRecurrence,
            validEvidence.EffectiveAuthority.AllowsExternalPublication,
            validEvidence.EffectiveAuthority.AllowsIrreversibleAction);
        var malformedCapabilities = validEvidence.CapabilityAdmission with { Requirements = null! };
        var authorityCandidate = NewEvidence(validEvidence, effectiveAuthority: malformedAuthority);
        var capabilityCandidate = NewEvidence(validEvidence, capabilityAdmission: malformedCapabilities);
        var referenceCandidate = NewEvidence(validEvidence, references: null, omitReferences: true);

        Assert.Null(authorityCandidate.EffectiveAuthority);
        Assert.Null(capabilityCandidate.CapabilityAdmission.Requirements);
        Assert.Null(referenceCandidate.References);
        Assert.False(GovernedLoopAdmissionValidator.Validate(authorityCandidate, intent).IsValid);
        Assert.False(GovernedLoopAdmissionValidator.Validate(capabilityCandidate, intent).IsValid);
        Assert.False(GovernedLoopAdmissionValidator.Validate(referenceCandidate, intent).IsValid);
    }

    [Fact]
    public void Oversized_caller_collections_are_snapshotted_only_to_limit_plus_one_and_fail_closed()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var valid = GovernedLoopAdmissionTestFixture.Evidence(intent);
        var oversizedAuthority = new AuthorityCeiling(
            valid.EffectiveAuthority.Capabilities,
            Enumerable.Repeat(
                valid.EffectiveAuthority.DataClasses[0],
                AuthorityContractLimits.MaxDataClassesPerCeiling + 100).ToArray(),
            valid.EffectiveAuthority.MaxTargetCount,
            valid.EffectiveAuthority.MaxSideEffectClass,
            valid.EffectiveAuthority.AllowsRecurrence,
            valid.EffectiveAuthority.AllowsExternalPublication,
            valid.EffectiveAuthority.AllowsIrreversibleAction);
        var oversizedCapabilities = valid.CapabilityAdmission with
        {
            Pins = Enumerable.Repeat(
                valid.CapabilityAdmission.Pins[0],
                CapabilityContractLimits.MaxCapabilityAdmissionPins + 100).ToArray()
        };
        var oversizedReferences = Enumerable.Repeat(
            valid.References[0],
            GovernedLoopAdmissionLimits.MaxEvidenceReferences + 100).ToArray();
        var candidate = new GovernedLoopAdmissionEvidence(
            valid.SchemaVersion,
            valid.IntentHash,
            valid.Binding,
            valid.GrantProfile,
            valid.GrantBoundary,
            valid.GrantDependencyEvidenceHash,
            oversizedAuthority,
            oversizedCapabilities,
            oversizedReferences,
            valid.EvaluatedAtUtc,
            valid.ContentHash);

        Assert.Equal(AuthorityContractLimits.MaxDataClassesPerCeiling + 1, candidate.EffectiveAuthority.DataClasses.Count);
        Assert.Equal(CapabilityContractLimits.MaxCapabilityAdmissionPins + 1, candidate.CapabilityAdmission.Pins.Count);
        Assert.Equal(GovernedLoopAdmissionLimits.MaxEvidenceReferences + 1, candidate.References.Count);
        Assert.False(GovernedLoopAdmissionValidator.Validate(candidate, intent).IsValid);
    }

    private static GovernedLoopAdmissionEvidence NewEvidence(
        GovernedLoopAdmissionEvidence value,
        AuthorityCeiling? effectiveAuthority = null,
        CapabilityAdmissionSnapshot? capabilityAdmission = null,
        IReadOnlyList<GovernedLoopAdmissionEvidenceReference>? references = null,
        bool omitReferences = false)
        => new(
            value.SchemaVersion,
            value.IntentHash,
            value.Binding,
            value.GrantProfile,
            value.GrantBoundary,
            value.GrantDependencyEvidenceHash,
            effectiveAuthority ?? value.EffectiveAuthority,
            capabilityAdmission ?? value.CapabilityAdmission,
            omitReferences ? null! : references ?? value.References,
            value.EvaluatedAtUtc,
            value.ContentHash);

    private static void AssertReadOnly<T>(IReadOnlyList<T> values)
        => Assert.Throws<NotSupportedException>(() => ((IList<T>)values).Clear());
}
