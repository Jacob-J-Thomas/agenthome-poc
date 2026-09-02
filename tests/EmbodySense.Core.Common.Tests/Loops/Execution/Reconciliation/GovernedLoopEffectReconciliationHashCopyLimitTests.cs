using System.Collections.Immutable;
using System.Runtime.InteropServices;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationHashCopyLimitTests
{
    [Fact]
    public void Hashes_are_culture_independent_and_cover_anti_replay_fields()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive);
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        string french;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            french = GovernedLoopEffectReconciliationContractHash.Compute(valid);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
        }

        Assert.Equal(valid.ContentHash, french);
        Assert.NotEqual(valid.ContentHash, GovernedLoopEffectReconciliationContractHash.Compute(valid with { CaseVersion = 2, PreviousContentHash = GovernedLoopEffectReconciliationTestFixture.Hash('0') }));
        Assert.NotEqual(valid.Binding.ContentHash, GovernedLoopEffectReconciliationContractHash.Compute(valid.Binding with { VisitOrdinal = 2 }));
        Assert.NotEqual(valid.ContractMetadata.ContentHash, GovernedLoopEffectReconciliationContractHash.Compute(valid.ContractMetadata with { ProbeContractVersion = 2 }));
        Assert.NotEqual(valid.AssessmentHistory[0].ContentHash, GovernedLoopEffectReconciliationContractHash.Compute(valid.AssessmentHistory[0] with { AuthorityEvidenceHash = GovernedLoopEffectReconciliationTestFixture.Hash('1') }));
    }

    [Fact]
    public void Create_rejects_unsorted_evidence_sources_instead_of_normalizing_them()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive);
        var first = GovernedLoopEffectReconciliationTestFixture.Source(valid.Binding, valid.ContractMetadata, "source-a");
        var second = GovernedLoopEffectReconciliationTestFixture.Source(valid.Binding, valid.ContractMetadata, "source-b");

        Assert.Throws<ArgumentException>(() => GovernedLoopEffectReconciliationContract.Create(valid.CaseId, 1, valid.Binding, valid.ContractMetadata, [second, first], [], valid.AssessmentHistory, valid.CurrentAssessmentHash, null, null, valid.CaseReceiptHashes, null, valid.OpenedAtUtc, valid.UpdatedAtUtc));
    }

    [Fact]
    public void Create_rejects_unsorted_observations_instead_of_normalizing_them()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied);
        var source = valid.EvidenceSources[0];
        var first = GovernedLoopEffectReconciliationTestFixture.Observation(valid.Binding, source, GovernedLoopEffectReconciliationObservedOutcome.NotApplied, "observation-a");
        var second = GovernedLoopEffectReconciliationTestFixture.Observation(valid.Binding, source, GovernedLoopEffectReconciliationObservedOutcome.NotApplied, "observation-b");
        var assessment = GovernedLoopEffectReconciliationTestFixture.Assessment(valid.Binding, GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, [first, second]);

        Assert.Throws<ArgumentException>(() => GovernedLoopEffectReconciliationContract.Create(valid.CaseId, 1, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, [second, first], [assessment], assessment.ContentHash, null, null, valid.CaseReceiptHashes, null, valid.OpenedAtUtc, valid.UpdatedAtUtc));
    }

    [Fact]
    public void Create_rejects_unsorted_assessments_instead_of_normalizing_them()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive);
        var first = GovernedLoopEffectReconciliationContractHash.Apply(valid.AssessmentHistory[0] with { AssessmentId = "assessment-a", ContentHash = string.Empty });
        var second = GovernedLoopEffectReconciliationContractHash.Apply(valid.AssessmentHistory[0] with { AssessmentId = "assessment-b", ContentHash = string.Empty });

        Assert.Throws<ArgumentException>(() => GovernedLoopEffectReconciliationContract.Create(valid.CaseId, 1, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, [], [second, first], second.ContentHash, null, null, valid.CaseReceiptHashes, null, valid.OpenedAtUtc, valid.UpdatedAtUtc));
    }

    [Fact]
    public void Create_rejects_unsorted_receipt_hashes_and_hashes_ordered_inputs_deterministically()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive);
        string[] ordered = [GovernedLoopEffectReconciliationTestFixture.Hash('a'), GovernedLoopEffectReconciliationTestFixture.Hash('b')];
        var first = GovernedLoopEffectReconciliationContract.Create(valid.CaseId, 1, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, valid.ObservationHistory, valid.AssessmentHistory, valid.CurrentAssessmentHash, null, null, ordered, null, valid.OpenedAtUtc, valid.UpdatedAtUtc);
        var second = GovernedLoopEffectReconciliationContract.Create(valid.CaseId, 1, valid.Binding, valid.ContractMetadata, valid.EvidenceSources.ToArray(), valid.ObservationHistory.ToArray(), valid.AssessmentHistory.ToArray(), valid.CurrentAssessmentHash, null, null, ordered.ToArray(), null, valid.OpenedAtUtc, valid.UpdatedAtUtc);

        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectReconciliationContract.Create(valid.CaseId, 1, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, valid.ObservationHistory, valid.AssessmentHistory, valid.CurrentAssessmentHash, null, null, ordered.Reverse().ToArray(), null, valid.OpenedAtUtc, valid.UpdatedAtUtc));
    }

    [Fact]
    public void Public_copy_boundary_detaches_hostile_immutable_collection_backing_arrays()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Conflicting);
        var observationBacking = valid.ObservationHistory.ToArray();
        var hashBacking = valid.AssessmentHistory[0].ObservationHashes.ToArray();
        var originalAssessment = valid.AssessmentHistory[0];
        var hostileAssessment = new GovernedLoopEffectReconciliationAssessment(originalAssessment.SchemaVersion, originalAssessment.CaseId, originalAssessment.BindingHash, originalAssessment.AssessmentId, originalAssessment.Kind, ImmutableCollectionsMarshal.AsImmutableArray(hashBacking), originalAssessment.AuthorityEvidenceHash, originalAssessment.AssessedAtUtc, originalAssessment.SafeDetail, originalAssessment.ContentHash);
        var hostileCase = new GovernedLoopEffectReconciliationCase(valid.SchemaVersion, valid.CaseId, valid.CaseVersion, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, ImmutableCollectionsMarshal.AsImmutableArray(observationBacking), [hostileAssessment], valid.CurrentAssessmentHash, valid.Disposition, valid.Resolution, valid.CaseReceiptHashes, valid.PreviousContentHash, valid.OpenedAtUtc, valid.UpdatedAtUtc, valid.ContentHash);

        var detached = GovernedLoopEffectReconciliationContractCopy.Copy(hostileCase);
        observationBacking[0] = observationBacking[0] with { ObservationId = "mutated" };
        hashBacking[0] = GovernedLoopEffectReconciliationTestFixture.Hash('0');

        Assert.NotEqual("mutated", detached.ObservationHistory[0].ObservationId);
        Assert.DoesNotContain(GovernedLoopEffectReconciliationTestFixture.Hash('0'), detached.AssessmentHistory[0].ObservationHashes);
    }

    [Fact]
    public void Identifier_hash_summary_and_detail_bounds_accept_exact_max_and_reject_max_plus_one()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive);
        var exactSource = GovernedLoopEffectReconciliationContractHash.Apply(valid.EvidenceSources[0] with { SourceId = new string('s', GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters) });
        var exactObservation = GovernedLoopEffectReconciliationContractHash.Apply(GovernedLoopEffectReconciliationTestFixture.Observation(valid.Binding, valid.EvidenceSources[0], GovernedLoopEffectReconciliationObservedOutcome.Unknown, kind: GovernedLoopEffectReconciliationObservationKind.Prose) with { SafeSummary = new string('s', GovernedLoopEffectReconciliationContractLimits.MaxSummaryCharacters) });
        var exactAssessment = GovernedLoopEffectReconciliationContractHash.Apply(valid.AssessmentHistory[0] with { SafeDetail = new string('d', GovernedLoopEffectReconciliationContractLimits.MaxDetailCharacters) });

        Assert.True(GovernedLoopEffectReconciliationContractValidator.Validate(exactSource).IsValid);
        Assert.True(GovernedLoopEffectReconciliationContractValidator.Validate(exactObservation).IsValid);
        Assert.True(GovernedLoopEffectReconciliationContractValidator.Validate(exactAssessment).IsValid);
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectReconciliationContractHash.Apply(exactSource with { SourceId = new string('s', GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters + 1) }));
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectReconciliationContractHash.Apply(exactObservation with { SafeSummary = new string('s', GovernedLoopEffectReconciliationContractLimits.MaxSummaryCharacters + 1) }));
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectReconciliationContractHash.Apply(exactAssessment with { SafeDetail = new string('d', GovernedLoopEffectReconciliationContractLimits.MaxDetailCharacters + 1) }));
        Assert.False(GovernedLoopEffectReconciliationContractValidator.Validate(valid.Binding with { IntentHash = new string('A', 64) }).IsValid);
    }

    [Fact]
    public void Observation_assessment_and_receipt_collection_bounds_reject_bound_plus_one()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive);
        var source = valid.EvidenceSources[0];
        var observations = Enumerable.Range(0, GovernedLoopEffectReconciliationContractLimits.MaxObservations + 1)
            .Select(index => GovernedLoopEffectReconciliationTestFixture.Observation(valid.Binding, source, GovernedLoopEffectReconciliationObservedOutcome.Unknown, $"observation-{index:D2}", GovernedLoopEffectReconciliationObservationKind.Missing))
            .ToArray();
        var assessments = Enumerable.Range(0, GovernedLoopEffectReconciliationContractLimits.MaxAssessments + 1)
            .Select(index => GovernedLoopEffectReconciliationContractHash.Apply(valid.AssessmentHistory[0] with { AssessmentId = $"assessment-{index:D2}" }))
            .ToArray();
        var receipts = Enumerable.Range(0, GovernedLoopEffectReconciliationContractLimits.MaxCaseReceipts + 1)
            .Select(index => index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        var exactObservationCase = GovernedLoopEffectReconciliationContract.Create(valid.CaseId, 1, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, observations[..^1], valid.AssessmentHistory, valid.CurrentAssessmentHash, null, null, valid.CaseReceiptHashes, null, valid.OpenedAtUtc, valid.UpdatedAtUtc);
        var exactAssessmentCase = GovernedLoopEffectReconciliationContract.Create(valid.CaseId, 1, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, [], assessments[..^1], assessments[^2].ContentHash, null, null, valid.CaseReceiptHashes, null, valid.OpenedAtUtc, valid.UpdatedAtUtc);
        var exactReceiptCase = GovernedLoopEffectReconciliationContract.Create(valid.CaseId, 1, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, [], valid.AssessmentHistory, valid.CurrentAssessmentHash, null, null, receipts[..^1], null, valid.OpenedAtUtc, valid.UpdatedAtUtc);

        Assert.True(GovernedLoopEffectReconciliationContract.Validate(exactObservationCase).IsValid);
        Assert.True(GovernedLoopEffectReconciliationContract.Validate(exactAssessmentCase).IsValid);
        Assert.True(GovernedLoopEffectReconciliationContract.Validate(exactReceiptCase).IsValid);
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectReconciliationContract.Create(valid.CaseId, 1, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, observations, valid.AssessmentHistory, valid.CurrentAssessmentHash, null, null, valid.CaseReceiptHashes, null, valid.OpenedAtUtc, valid.UpdatedAtUtc));
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectReconciliationContract.Create(valid.CaseId, 1, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, [], assessments, assessments[^1].ContentHash, null, null, valid.CaseReceiptHashes, null, valid.OpenedAtUtc, valid.UpdatedAtUtc));
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectReconciliationContract.Create(valid.CaseId, 1, valid.Binding, valid.ContractMetadata, valid.EvidenceSources, [], valid.AssessmentHistory, valid.CurrentAssessmentHash, null, null, receipts, null, valid.OpenedAtUtc, valid.UpdatedAtUtc));
    }

    [Fact]
    public void Observation_reference_limit_accepts_max_and_rejects_max_plus_one()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive);
        var max = Enumerable.Range(0, GovernedLoopEffectReconciliationContractLimits.MaxObservationReferences)
            .Select(index => index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        var original = valid.AssessmentHistory[0];
        var exact = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationAssessment(original.SchemaVersion, original.CaseId, original.BindingHash, original.AssessmentId, original.Kind, max, original.AuthorityEvidenceHash, original.AssessedAtUtc, original.SafeDetail, string.Empty));

        Assert.True(GovernedLoopEffectReconciliationContractValidator.Validate(exact).IsValid);
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationAssessment(original.SchemaVersion, original.CaseId, original.BindingHash, original.AssessmentId, original.Kind, max.Append(GovernedLoopEffectReconciliationTestFixture.Hash('f')).Order(StringComparer.Ordinal).ToArray(), original.AuthorityEvidenceHash, original.AssessedAtUtc, original.SafeDetail, string.Empty)));
    }

    [Fact]
    public void Malformed_collections_return_bounded_safe_errors_without_throwing()
    {
        var invalid = new GovernedLoopEffectReconciliationCase(
            2,
            "INVALID VALUE",
            0,
            null!,
            null!,
            Enumerable.Repeat<GovernedLoopEffectReconciliationEvidenceSource>(null!, GovernedLoopEffectReconciliationContractLimits.MaxEvidenceSources + 1).ToArray(),
            Enumerable.Repeat<GovernedLoopEffectReconciliationObservation>(null!, GovernedLoopEffectReconciliationContractLimits.MaxObservations + 1).ToArray(),
            Enumerable.Repeat<GovernedLoopEffectReconciliationAssessment>(null!, GovernedLoopEffectReconciliationContractLimits.MaxAssessments + 1).ToArray(),
            null,
            null,
            null,
            Enumerable.Repeat("INVALID", GovernedLoopEffectReconciliationContractLimits.MaxCaseReceipts + 1).ToArray(),
            null,
            default,
            default,
            string.Empty);

        var exception = Record.Exception(() => GovernedLoopEffectReconciliationContractValidator.Validate(invalid));
        var result = GovernedLoopEffectReconciliationContractValidator.Validate(invalid);

        Assert.Null(exception);
        Assert.False(result.IsValid);
        Assert.InRange(result.Errors.Count, 1, GovernedLoopEffectReconciliationContractLimits.MaxValidationErrors);
        Assert.All(result.Errors, error => Assert.InRange(error.Path.Length, 1, GovernedLoopEffectReconciliationContractLimits.MaxErrorPathCharacters));
    }
}
