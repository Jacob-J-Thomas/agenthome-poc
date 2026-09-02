using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationAttemptContractTests
{
    [Theory]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied, GovernedLoopEffectOutcome.NotApplied)]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedSucceeded, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied, GovernedLoopEffectOutcome.Succeeded)]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedFailed, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied, GovernedLoopEffectOutcome.Failed)]
    public void Exact_accepted_proof_creates_only_typed_hash_chained_successor(GovernedLoopEffectReconciliationAssessmentKind assessment, GovernedLoopEffectReconciliationDispositionKind disposition, GovernedLoopEffectOutcome outcome)
    {
        var current = GovernedLoopEffectReconciliationTestFixture.CurrentAttempt();
        var reconciliationCase = GovernedLoopEffectReconciliationTestFixture.Case(assessment, disposition, includeResolution: true, current);

        var successor = GovernedLoopEffectReconciliationAttemptContract.CreateSuccessor(current, reconciliationCase);

        Assert.Equal(GovernedLoopEffectPhase.Reconciled, successor.Payload.Phase);
        Assert.Equal(outcome, successor.Payload.Outcome);
        Assert.Equal(current.ContentHash, successor.PreviousContentHash);
        Assert.Equal(current.Payload.IntentHash, successor.Payload.IntentHash);
        Assert.True(GovernedLoopEffectReconciliationAttemptContract.IsDirectSuccessor(current, successor, reconciliationCase));
        Assert.False(GovernedLoopEffectAttemptContract.IsDirectSuccessor(current, successor));
    }

    [Theory]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive)]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.Conflicting)]
    [InlineData(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedOutcomeUnknown)]
    public void Unresolved_assessments_quarantine_and_never_create_successor(GovernedLoopEffectReconciliationAssessmentKind assessment)
    {
        var current = GovernedLoopEffectReconciliationTestFixture.CurrentAttempt();
        var reconciliationCase = GovernedLoopEffectReconciliationTestFixture.Case(assessment, GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved, attempt: current);

        Assert.False(GovernedLoopEffectReconciliationAttemptContract.CanCreateSuccessor(current, reconciliationCase));
        Assert.Throws<InvalidOperationException>(() => GovernedLoopEffectReconciliationAttemptContract.CreateSuccessor(current, reconciliationCase));
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, current.Payload.Phase);
    }

    [Fact]
    public void Generic_advance_cannot_create_reconciled_not_applied_or_known_outcomes()
    {
        var current = GovernedLoopEffectReconciliationTestFixture.CurrentAttempt();

        Assert.NotNull(Record.Exception(() => GovernedLoopEffectAttemptContract.Advance(current, GovernedLoopEffectPhase.Reconciled, GovernedLoopEffectOutcome.NotApplied, GovernedLoopEffectEvidenceStatus.Complete, null, "reconciliation", current.Payload.UpdatedAtUtc.AddMinutes(1))));
        Assert.NotNull(Record.Exception(() => GovernedLoopEffectAttemptContract.Advance(current, GovernedLoopEffectPhase.Reconciled, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome", "reconciliation", current.Payload.UpdatedAtUtc.AddMinutes(1))));
        Assert.False(GovernedLoopExecutionStateMatrix.IsEffectDispatchEligible(GovernedLoopEffectPayload.Create(1, current.Payload.EffectId, current.Payload.OperationId, current.Payload.EffectGeneration, current.Payload.Origin, current.Payload.OriginNodeId, current.Payload.IntentHash, GovernedLoopEffectPhase.Reconciled, GovernedLoopEffectOutcome.NotApplied, GovernedLoopEffectEvidenceStatus.Complete, null, "reconciliation", current.Payload.UpdatedAtUtc.AddMinutes(1))));
    }

    [Fact]
    public void Generic_execution_transition_gate_accepts_the_exact_typed_not_applied_successor()
    {
        var current = GovernedLoopEffectReconciliationTestFixture.CurrentAttempt();
        var reconciliationCase = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied, includeResolution: true, current);
        var successor = GovernedLoopEffectReconciliationAttemptContract.CreateSuccessor(current, reconciliationCase);
        var currentPosture = GovernedLoopEffectPosture.Create(current.Binding, current.Payload);
        var successorPosture = GovernedLoopEffectPosture.Create(successor.Binding, successor.Payload);

        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(currentPosture, successorPosture).IsValid);
    }

    [Theory]
    [InlineData(GovernedLoopEffectOutcome.Succeeded)]
    [InlineData(GovernedLoopEffectOutcome.Failed)]
    [InlineData(GovernedLoopEffectOutcome.Conflicted)]
    public void Generic_execution_transition_gate_rejects_other_outcome_changes(GovernedLoopEffectOutcome outcome)
    {
        var current = GovernedLoopEffectReconciliationTestFixture.CurrentAttempt();
        var changedPayload = GovernedLoopEffectPayload.Create(
            current.Payload.SchemaVersion,
            current.Payload.EffectId,
            current.Payload.OperationId,
            current.Payload.EffectGeneration,
            current.Payload.Origin,
            current.Payload.OriginNodeId,
            current.Payload.IntentHash,
            GovernedLoopEffectPhase.Reconciled,
            outcome,
            GovernedLoopEffectEvidenceStatus.Complete,
            "outcome-evidence",
            "reconciliation-evidence",
            current.Payload.UpdatedAtUtc.AddMinutes(1));
        var currentPosture = GovernedLoopEffectPosture.Create(current.Binding, current.Payload);
        var changedPosture = GovernedLoopEffectPosture.Create(current.Binding, changedPayload);

        Assert.False(GovernedLoopExecutionValidator.ValidateTransition(currentPosture, changedPosture).IsValid);
    }

    [Fact]
    public void Not_applied_is_never_success_and_never_implies_retry_or_dispatch()
    {
        Assert.NotEqual(GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectOutcome.NotApplied);
        Assert.NotEqual(GovernedLoopEffectOutcome.Failed, GovernedLoopEffectOutcome.NotApplied);
        Assert.Null(GovernedLoopEffectReconciliationStateMatrix.GetAcceptedOutcome(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive));
        Assert.Null(GovernedLoopEffectReconciliationStateMatrix.GetAcceptedOutcome(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedOutcomeUnknown));
    }

    [Fact]
    public void Cross_attempt_case_and_tampered_successor_fail_closed()
    {
        var current = GovernedLoopEffectReconciliationTestFixture.CurrentAttempt();
        var reconciliationCase = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied, includeResolution: true, current);
        var successor = GovernedLoopEffectReconciliationAttemptContract.CreateSuccessor(current, reconciliationCase);
        var other = current with { ContentHash = GovernedLoopEffectReconciliationTestFixture.Hash('0') };

        Assert.False(GovernedLoopEffectReconciliationAttemptContract.CanCreateSuccessor(other, reconciliationCase));
        Assert.False(GovernedLoopEffectReconciliationAttemptContract.IsDirectSuccessor(current, successor with { PreviousContentHash = GovernedLoopEffectReconciliationTestFixture.Hash('0') }, reconciliationCase));
    }
}
