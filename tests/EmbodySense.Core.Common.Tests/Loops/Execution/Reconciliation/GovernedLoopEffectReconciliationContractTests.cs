using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationContractTests
{
    [Fact]
    public void Every_public_evidence_validator_accepts_exact_hashes_and_rejects_absent_contracts()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedSucceeded, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied, includeResolution: true);

        Assert.True(GovernedLoopEffectReconciliationContractValidator.Validate(valid.Binding).IsValid);
        Assert.True(GovernedLoopEffectReconciliationContractValidator.Validate(valid.ContractMetadata).IsValid);
        Assert.True(GovernedLoopEffectReconciliationContractValidator.Validate(valid.EvidenceSources[0]).IsValid);
        Assert.True(GovernedLoopEffectReconciliationContractValidator.Validate(valid.ObservationHistory[0]).IsValid);
        Assert.True(GovernedLoopEffectReconciliationContractValidator.Validate(valid.AssessmentHistory[0]).IsValid);
        Assert.True(GovernedLoopEffectReconciliationContractValidator.Validate(valid.Disposition).IsValid);
        Assert.True(GovernedLoopEffectReconciliationContractValidator.Validate(valid.Resolution).IsValid);
        Assert.False(GovernedLoopEffectReconciliationContractValidator.Validate((GovernedLoopEffectReconciliationBinding?)null).IsValid);
        Assert.False(GovernedLoopEffectReconciliationContractValidator.Validate((GovernedLoopEffectReconciliationContractMetadata?)null).IsValid);
        Assert.False(GovernedLoopEffectReconciliationContractValidator.Validate((GovernedLoopEffectReconciliationEvidenceSource?)null).IsValid);
        Assert.False(GovernedLoopEffectReconciliationContractValidator.Validate((GovernedLoopEffectReconciliationObservation?)null).IsValid);
        Assert.False(GovernedLoopEffectReconciliationContractValidator.Validate((GovernedLoopEffectReconciliationAssessment?)null).IsValid);
        Assert.False(GovernedLoopEffectReconciliationContractValidator.Validate((GovernedLoopEffectReconciliationDisposition?)null).IsValid);
        Assert.False(GovernedLoopEffectReconciliationContractValidator.Validate((GovernedLoopEffectReconciliationResolution?)null).IsValid);
        Assert.Contains("Required", GovernedLoopEffectReconciliationContractValidator.Validate((GovernedLoopEffectReconciliationBinding?)null).Errors[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_creation_requires_the_exact_reconciliation_required_attempt_and_bounded_visit()
    {
        var current = GovernedLoopEffectReconciliationTestFixture.CurrentAttempt();
        var prepared = Effects.GovernedLoopEffectAttemptContractTests.Prepare();

        Assert.Throws<ArgumentException>(() => GovernedLoopEffectReconciliationContract.CreateBinding(GovernedLoopExecutionTestFixture.WorkspaceId, 0, 1, prepared));
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectReconciliationContract.CreateBinding(GovernedLoopExecutionTestFixture.WorkspaceId, GovernedLoopExecutionLimits.MaxFrontierNodes, 1, current));
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectReconciliationContract.CreateBinding(GovernedLoopExecutionTestFixture.WorkspaceId, 0, GovernedLoopExecutionLimits.MaxNodeVisits + 1, current));
    }

    [Fact]
    public void Open_observed_assessed_quarantined_and_accepted_stages_are_explicit()
    {
        var attempt = GovernedLoopEffectReconciliationTestFixture.CurrentAttempt();
        var binding = GovernedLoopEffectReconciliationTestFixture.Binding(attempt);
        var metadata = GovernedLoopEffectReconciliationTestFixture.Metadata(attempt);
        var open = GovernedLoopEffectReconciliationContract.Open(GovernedLoopEffectReconciliationTestFixture.CaseId, binding, metadata, [], [], GovernedLoopEffectReconciliationTestFixture.OpenedAtUtc);
        var assessed = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive);
        var quarantined = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive, GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved);
        var accepted = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied, includeResolution: true);

        Assert.True(GovernedLoopEffectReconciliationContract.Validate(open, attempt).IsValid);
        Assert.True(GovernedLoopEffectReconciliationContract.Validate(assessed, attempt).IsValid);
        Assert.Null(assessed.Disposition);
        Assert.True(GovernedLoopEffectReconciliationContract.Validate(quarantined, attempt).IsValid);
        Assert.Null(quarantined.Resolution);
        Assert.True(GovernedLoopEffectReconciliationContract.Validate(accepted, attempt).IsValid);
        Assert.NotNull(accepted.Resolution);
    }

    [Theory]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive, GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved)]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.Conflicting, GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved)]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedOutcomeUnknown, GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved)]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied)]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedSucceeded, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied)]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedFailed, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied)]
    public void Closed_assessment_disposition_matrix_accepts_only_exact_mapping(GovernedLoopEffectReconciliationAssessmentKind assessment, GovernedLoopEffectReconciliationDispositionKind disposition)
    {
        var reconciliationCase = GovernedLoopEffectReconciliationTestFixture.Case(assessment, disposition);

        Assert.True(GovernedLoopEffectReconciliationContract.Validate(reconciliationCase).IsValid);
    }

    [Fact]
    public void Disposition_without_current_assessment_and_resolution_without_acceptance_fail_closed()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive, GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved);

        Assert.False(GovernedLoopEffectReconciliationContract.Validate(valid with { CurrentAssessmentHash = null }).IsValid);
        var acceptedResolution = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied, includeResolution: true).Resolution;
        var resolutionWithoutAcceptance = new GovernedLoopEffectReconciliationCase(valid.SchemaVersion, valid.CaseId, valid.CaseVersion, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, valid.ObservationHistory, valid.AssessmentHistory, valid.CurrentAssessmentHash, valid.Disposition, acceptedResolution, valid.CaseReceiptHashes, valid.PreviousContentHash, valid.OpenedAtUtc, valid.UpdatedAtUtc, valid.ContentHash);
        Assert.False(GovernedLoopEffectReconciliationContract.Validate(resolutionWithoutAcceptance).IsValid);
    }

    [Fact]
    public void Empty_evidence_is_legal_only_for_inconclusive_assessment()
    {
        var inconclusive = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive);
        var proved = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied);
        var originalAssessment = proved.AssessmentHistory[0];
        var emptyProved = new GovernedLoopEffectReconciliationAssessment(originalAssessment.SchemaVersion, originalAssessment.CaseId, originalAssessment.BindingHash, originalAssessment.AssessmentId, originalAssessment.Kind, [], originalAssessment.AuthorityEvidenceHash, originalAssessment.AssessedAtUtc, originalAssessment.SafeDetail, string.Empty);

        Assert.True(GovernedLoopEffectReconciliationContract.Validate(inconclusive).IsValid);
        Assert.False(GovernedLoopEffectReconciliationContractValidator.Validate(emptyProved).IsValid);
    }

    [Fact]
    public void Conflicting_requires_two_exact_fresh_authoritative_contradictions()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Conflicting);
        var originalAssessment = valid.AssessmentHistory[0];
        var oneHash = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationAssessment(originalAssessment.SchemaVersion, originalAssessment.CaseId, originalAssessment.BindingHash, originalAssessment.AssessmentId, originalAssessment.Kind, [valid.ObservationHistory[0].ContentHash], originalAssessment.AuthorityEvidenceHash, originalAssessment.AssessedAtUtc, originalAssessment.SafeDetail, string.Empty));
        var invalid = new GovernedLoopEffectReconciliationCase(valid.SchemaVersion, valid.CaseId, valid.CaseVersion, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, valid.ObservationHistory, [oneHash], oneHash.ContentHash, null, null, valid.CaseReceiptHashes, null, valid.OpenedAtUtc, valid.UpdatedAtUtc, string.Empty);

        Assert.True(GovernedLoopEffectReconciliationContract.Validate(valid).IsValid);
        Assert.False(GovernedLoopEffectReconciliationContract.Validate(invalid).IsValid);
    }

    [Fact]
    public void Zero_fresh_authoritative_outcomes_allow_only_inconclusive()
    {
        var baseline = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive);
        var source = GovernedLoopEffectReconciliationTestFixture.Source(baseline.Binding, baseline.ContractMetadata, reliability: GovernedLoopEffectReconciliationReliabilityPosture.Corroborating);
        var observation = GovernedLoopEffectReconciliationTestFixture.Observation(baseline.Binding, source, GovernedLoopEffectReconciliationObservedOutcome.NotApplied);
        var assessment = GovernedLoopEffectReconciliationTestFixture.Assessment(baseline.Binding, GovernedLoopEffectReconciliationAssessmentKind.Inconclusive, [observation]);
        var valid = GovernedLoopEffectReconciliationContract.Create(baseline.CaseId, 1, baseline.Binding, baseline.ContractMetadata, [source], [observation], [assessment], assessment.ContentHash, null, null, baseline.CaseReceiptHashes, null, baseline.OpenedAtUtc, assessment.AssessedAtUtc);

        Assert.True(GovernedLoopEffectReconciliationContract.Validate(valid).IsValid);
        AssertWrongClassificationsRejected(valid, GovernedLoopEffectReconciliationAssessmentKind.Inconclusive);
    }

    [Fact]
    public void Conflicting_fresh_authoritative_outcomes_allow_only_conflicting()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Conflicting);

        Assert.True(GovernedLoopEffectReconciliationContract.Validate(valid).IsValid);
        AssertWrongClassificationsRejected(valid, GovernedLoopEffectReconciliationAssessmentKind.Conflicting);
    }

    [Theory]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied)]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedSucceeded)]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedFailed)]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedOutcomeUnknown)]
    public void One_fresh_authoritative_outcome_allows_only_its_matching_proved_assessment(GovernedLoopEffectReconciliationAssessmentKind expected)
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(expected);

        Assert.True(GovernedLoopEffectReconciliationContract.Validate(valid).IsValid);
        AssertWrongClassificationsRejected(valid, expected);
    }

    [Theory]
    [InlineData(GovernedLoopEffectReconciliationObservationKind.Missing)]
    [InlineData(GovernedLoopEffectReconciliationObservationKind.TimedOut)]
    [InlineData(GovernedLoopEffectReconciliationObservationKind.Cancelled)]
    [InlineData(GovernedLoopEffectReconciliationObservationKind.Prose)]
    [InlineData(GovernedLoopEffectReconciliationObservationKind.CallerAssertion)]
    [InlineData(GovernedLoopEffectReconciliationObservationKind.UnprovenHash)]
    public void Non_evidence_observations_never_prove_outcome(GovernedLoopEffectReconciliationObservationKind kind)
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedSucceeded);
        var source = valid.EvidenceSources[0];
        var nonProof = GovernedLoopEffectReconciliationTestFixture.Observation(
            valid.Binding,
            source,
            kind is GovernedLoopEffectReconciliationObservationKind.Missing or GovernedLoopEffectReconciliationObservationKind.TimedOut or GovernedLoopEffectReconciliationObservationKind.Cancelled
                ? GovernedLoopEffectReconciliationObservedOutcome.Unknown
                : GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded,
            kind: kind,
            evidenceReference: kind == GovernedLoopEffectReconciliationObservationKind.UnprovenHash ? "unproven" : null,
            evidenceHash: kind == GovernedLoopEffectReconciliationObservationKind.UnprovenHash ? GovernedLoopEffectReconciliationTestFixture.Hash('1') : null);
        var assessment = GovernedLoopEffectReconciliationTestFixture.Assessment(valid.Binding, GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedSucceeded, [nonProof]);
        var invalid = new GovernedLoopEffectReconciliationCase(valid.SchemaVersion, valid.CaseId, valid.CaseVersion, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, [nonProof], [assessment], assessment.ContentHash, null, null, valid.CaseReceiptHashes, null, valid.OpenedAtUtc, assessment.AssessedAtUtc, string.Empty);

        Assert.False(GovernedLoopEffectReconciliationContract.Validate(invalid).IsValid);
    }

    [Fact]
    public void Stale_and_non_authoritative_observations_never_prove_outcome()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied);
        var source = valid.EvidenceSources[0];
        var stale = GovernedLoopEffectReconciliationTestFixture.Observation(valid.Binding, source, GovernedLoopEffectReconciliationObservedOutcome.NotApplied, observedAtUtc: valid.OpenedAtUtc.AddTicks(-1));
        var staleAssessment = GovernedLoopEffectReconciliationTestFixture.Assessment(valid.Binding, GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, [stale]);
        var staleCase = new GovernedLoopEffectReconciliationCase(valid.SchemaVersion, valid.CaseId, valid.CaseVersion, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, [stale], [staleAssessment], staleAssessment.ContentHash, null, null, valid.CaseReceiptHashes, null, valid.OpenedAtUtc, staleAssessment.AssessedAtUtc, string.Empty);
        var informationalSource = GovernedLoopEffectReconciliationTestFixture.Source(valid.Binding, valid.ContractMetadata, reliability: GovernedLoopEffectReconciliationReliabilityPosture.Corroborating);
        var informational = GovernedLoopEffectReconciliationTestFixture.Observation(valid.Binding, informationalSource, GovernedLoopEffectReconciliationObservedOutcome.NotApplied);
        var informationalAssessment = GovernedLoopEffectReconciliationTestFixture.Assessment(valid.Binding, GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, [informational]);
        var informationalCase = new GovernedLoopEffectReconciliationCase(valid.SchemaVersion, valid.CaseId, valid.CaseVersion, valid.Binding, valid.ContractMetadata, [informationalSource], [informational], [informationalAssessment], informationalAssessment.ContentHash, null, null, valid.CaseReceiptHashes, null, valid.OpenedAtUtc, informationalAssessment.AssessedAtUtc, string.Empty);

        Assert.False(GovernedLoopEffectReconciliationContract.Validate(staleCase).IsValid);
        Assert.False(GovernedLoopEffectReconciliationContract.Validate(informationalCase).IsValid);
    }

    [Fact]
    public void Cross_case_binding_and_current_attempt_hash_substitution_fail_closed()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive);
        var reboundAssessment = GovernedLoopEffectReconciliationContractHash.Apply(valid.AssessmentHistory[0] with { CaseId = "other-case" });
        var crossCase = new GovernedLoopEffectReconciliationCase(valid.SchemaVersion, valid.CaseId, valid.CaseVersion, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, valid.ObservationHistory, [reboundAssessment], reboundAssessment.ContentHash, valid.Disposition, valid.Resolution, valid.CaseReceiptHashes, valid.PreviousContentHash, valid.OpenedAtUtc, valid.UpdatedAtUtc, valid.ContentHash);
        var alteredBinding = GovernedLoopEffectReconciliationContractHash.Apply(valid.Binding with { CurrentAttemptHash = GovernedLoopEffectReconciliationTestFixture.Hash('0') });

        Assert.False(GovernedLoopEffectReconciliationContract.Validate(crossCase).IsValid);
        var reboundCase = new GovernedLoopEffectReconciliationCase(valid.SchemaVersion, valid.CaseId, valid.CaseVersion, alteredBinding, valid.ContractMetadata, valid.EvidenceSources, valid.ObservationHistory, valid.AssessmentHistory, valid.CurrentAssessmentHash, valid.Disposition, valid.Resolution, valid.CaseReceiptHashes, valid.PreviousContentHash, valid.OpenedAtUtc, valid.UpdatedAtUtc, valid.ContentHash);
        Assert.False(GovernedLoopEffectReconciliationContract.Validate(reboundCase, GovernedLoopEffectReconciliationTestFixture.CurrentAttempt()).IsValid);
    }

    [Fact]
    public void Case_versions_require_contiguous_hash_chain_and_append_only_histories()
    {
        var first = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive);
        var second = GovernedLoopEffectReconciliationContract.Create(first.CaseId, 2, first.Binding, first.ContractMetadata, first.EvidenceSources, first.ObservationHistory, first.AssessmentHistory, first.CurrentAssessmentHash, first.Disposition, first.Resolution, first.CaseReceiptHashes, first.ContentHash, first.OpenedAtUtc, first.UpdatedAtUtc.AddMinutes(1));

        Assert.True(GovernedLoopEffectReconciliationContract.ValidateTransition(first, second).IsValid);
        Assert.False(GovernedLoopEffectReconciliationContract.ValidateTransition(first, second with { PreviousContentHash = GovernedLoopEffectReconciliationTestFixture.Hash('0') }).IsValid);
        Assert.False(GovernedLoopEffectReconciliationContract.ValidateTransition(first, second with { CaseVersion = 3 }).IsValid);
    }

    private static void AssertWrongClassificationsRejected(GovernedLoopEffectReconciliationCase valid, GovernedLoopEffectReconciliationAssessmentKind expected)
    {
        foreach (var wrong in Enum.GetValues<GovernedLoopEffectReconciliationAssessmentKind>().Where(kind => kind is not GovernedLoopEffectReconciliationAssessmentKind.Unknown && kind != expected))
        {
            var reclassified = GovernedLoopEffectReconciliationContractHash.Apply(valid.AssessmentHistory[0] with { Kind = wrong, ContentHash = string.Empty });
            var invalid = new GovernedLoopEffectReconciliationCase(
                valid.SchemaVersion,
                valid.CaseId,
                valid.CaseVersion,
                valid.Binding,
                valid.ContractMetadata,
                valid.EvidenceSources,
                valid.ObservationHistory,
                [reclassified],
                reclassified.ContentHash,
                null,
                null,
                valid.CaseReceiptHashes,
                valid.PreviousContentHash,
                valid.OpenedAtUtc,
                valid.UpdatedAtUtc,
                string.Empty);

            var result = GovernedLoopEffectReconciliationContract.Validate(invalid);

            Assert.Contains(result.Errors, error => error.Code == GovernedLoopEffectReconciliationValidationErrorCode.InvalidComposition && error.Path == "$case.assessmentHistory[0]");
        }
    }
}
