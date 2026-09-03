using EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed class GovernedLoopEffectReconciliationSurfaceModelTests
{
    [Fact]
    public void Public_models_enforce_value_free_finite_and_status_bound_shapes()
    {
        var hash = GovernedLoopEffectReconciliationStartupTestFixture.Hash("surface-model");
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var reference = new GovernedLoopEffectReconciliationCaseReference("case-surface-model", 1, hash, hash);
        var contract = new GovernedLoopEffectReconciliationContractProjection("contract-surface-model", 1, hash, "probe-surface-model", 1, hash);
        var detail = new GovernedLoopEffectReconciliationCaseDetail(reference, GovernedLoopEffectReconciliationCasePosture.Open, contract, [], [], [], null, null, [], now, now);
        var summary = new GovernedLoopEffectReconciliationCaseSummary(reference, GovernedLoopEffectReconciliationCasePosture.Open);
        var resolution = new GovernedLoopEffectReconciliationResolutionProjection("resolution-surface-model", hash, hash, GovernedLoopEffectReconciliationResolutionOutcome.NotApplied, null, null, now, hash);
        var observationHashes = new[] { hash };
        var assessment = new GovernedLoopEffectReconciliationAssessmentProjection("assessment-surface-model", GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, observationHashes, now, hash);

        observationHashes[0] = GovernedLoopEffectReconciliationStartupTestFixture.Hash("mutated-surface-model");
        Assert.Equal(hash, Assert.Single(assessment.ObservationHashes));

        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationCaseReference(reference.CaseId, 0, hash, hash));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationCaseReference(reference.CaseId, 1, hash.ToUpperInvariant(), hash));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationPageRequest(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationPageRequest(101));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationPageRequest(1, new string('c', 1_025)));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationPageRequest(1, "unsafe\ncursor"));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationAuthorizationRequest("workspace-sha256:" + hash.ToUpperInvariant(), "web", "effect-reconciliation", reference, hash));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationAuthorizationResult(GovernedLoopEffectReconciliationAuthorizationStatus.Unknown, hash));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationAuthorizationResult(GovernedLoopEffectReconciliationAuthorizationStatus.Ready, hash));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationAuthorizationResult(GovernedLoopEffectReconciliationAuthorizationStatus.Denied, hash, "actor", "scope", hash));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationCaseDetail(reference, GovernedLoopEffectReconciliationCasePosture.Open, contract, [], [], [], null, null, [], now.ToOffset(TimeSpan.FromHours(1)), now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationAssessmentProjection("assessment-surface-model", GovernedLoopEffectReconciliationAssessmentKind.Unknown, [hash], now, hash));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationEvidenceSourceProjection("source-surface-model", GovernedLoopEffectReconciliationEvidenceSourceKind.Unknown, GovernedLoopEffectReconciliationReliabilityPosture.Authoritative, hash, now, null, hash));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationObservationProjection("observation-surface-model", "source-surface-model", hash, GovernedLoopEffectReconciliationObservationKind.Evidence, GovernedLoopEffectReconciliationReliabilityPosture.Unknown, GovernedLoopEffectReconciliationObservedOutcome.NotApplied, null, null, now, now, hash));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationObservationProjection("observation-surface-model", "source-surface-model", hash, GovernedLoopEffectReconciliationObservationKind.Evidence, GovernedLoopEffectReconciliationReliabilityPosture.Authoritative, (GovernedLoopEffectReconciliationObservedOutcome)99, null, null, now, now, hash));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationResolutionProjection("resolution-surface-model", hash, hash, GovernedLoopEffectReconciliationResolutionOutcome.Unknown, null, null, now, hash));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationPage(GovernedLoopEffectReconciliationPageStatus.Ready, Enumerable.Repeat(summary, 101).ToArray()));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationPage(GovernedLoopEffectReconciliationPageStatus.Invalid, [summary]));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationProbeCatalogPage(GovernedLoopEffectReconciliationProbeCatalogStatus.Corrupt, [contract]));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationOperationResult(GovernedLoopEffectReconciliationOperationStatus.Denied, detail));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationReadResult(GovernedLoopEffectReconciliationReadStatus.Found, null));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationReadResult(GovernedLoopEffectReconciliationReadStatus.NotFound, detail));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.Found, null));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.NotFound, resolution));
    }
}
