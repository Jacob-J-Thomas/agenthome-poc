using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution;

public sealed class GovernedLoopExecutionStateMatrixTests
{
    [Fact]
    public void Closed_enums_reject_unknown_and_undefined_values()
    {
        Assert.False(GovernedLoopExecutionStateMatrix.IsSupported(GovernedLoopRunStatus.Unknown));
        Assert.False(GovernedLoopExecutionStateMatrix.IsSupported((GovernedLoopRunStatus)99));
        Assert.False(GovernedLoopExecutionStateMatrix.IsSupported(GovernedLoopNodeExecutionStatus.Unknown));
        Assert.False(GovernedLoopExecutionStateMatrix.IsSupported((GovernedLoopNodeExecutionStatus)99));
        Assert.False(GovernedLoopExecutionStateMatrix.IsSupported(GovernedLoopFrontierStatus.Unknown));
        Assert.False(GovernedLoopExecutionStateMatrix.IsSupported((GovernedLoopFrontierStatus)99));
        Assert.False(GovernedLoopExecutionStateMatrix.IsSupported(GovernedLoopEffectOrigin.Unknown));
        Assert.False(GovernedLoopExecutionStateMatrix.IsSupported((GovernedLoopEffectOrigin)99));
        Assert.False(GovernedLoopExecutionStateMatrix.IsSupported(GovernedLoopProjectionClass.Unknown));
        Assert.False(GovernedLoopExecutionStateMatrix.IsSupported((GovernedLoopProjectionClass)99));
        Assert.False(GovernedLoopExecutionStateMatrix.IsEffectOriginNodeValid(GovernedLoopEffectOrigin.Unknown, true));
        Assert.False(GovernedLoopExecutionStateMatrix.IsNodeEvidenceShapeValid(GovernedLoopNodeExecutionStatus.Unknown, null, false));
        Assert.False(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.Unknown, []));
        Assert.False(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.Active, null));
        Assert.False(GovernedLoopExecutionStateMatrix.IsProjectionStateValid(GovernedLoopProjectionClass.Unknown, GovernedLoopProjectionStatus.Pending, false, false, false));
    }

    [Fact]
    public void State_shape_matrices_match_the_complete_schema_one_truth_sets()
    {
        var actualNodes = new HashSet<(GovernedLoopNodeExecutionStatus Status, int? Attempt, bool HasOutcomeEvidence)>(
            from status in Enum.GetValues<GovernedLoopNodeExecutionStatus>()
            from attempt in new int?[] { null, 1 }
            from hasOutcomeEvidence in new[] { false, true }
            where GovernedLoopExecutionStateMatrix.IsNodeEvidenceShapeValid(status, attempt, hasOutcomeEvidence)
            select (status, attempt, hasOutcomeEvidence));
        HashSet<(GovernedLoopNodeExecutionStatus, int?, bool)> expectedNodes =
        [
            (GovernedLoopNodeExecutionStatus.Ready, null, false),
            (GovernedLoopNodeExecutionStatus.Running, 1, false),
            (GovernedLoopNodeExecutionStatus.Completed, 1, true),
            (GovernedLoopNodeExecutionStatus.Skipped, null, false),
            (GovernedLoopNodeExecutionStatus.Skipped, null, true),
            (GovernedLoopNodeExecutionStatus.Waiting, 1, false),
            (GovernedLoopNodeExecutionStatus.Failed, 1, true),
            (GovernedLoopNodeExecutionStatus.ReviewBlocked, 1, false)
        ];
        AssertSetEqual(expectedNodes, actualNodes);

        var actualEffects = new HashSet<(GovernedLoopEffectPhase Phase, GovernedLoopEffectOutcome Outcome, GovernedLoopEffectEvidenceStatus Evidence, bool HasOutcomeEvidence, bool HasReconciliationEvidence)>(
            from phase in Enum.GetValues<GovernedLoopEffectPhase>()
            from outcome in Enum.GetValues<GovernedLoopEffectOutcome>()
            from evidence in Enum.GetValues<GovernedLoopEffectEvidenceStatus>()
            from hasOutcomeEvidence in new[] { false, true }
            from hasReconciliationEvidence in new[] { false, true }
            where GovernedLoopExecutionStateMatrix.IsEffectStateValid(phase, outcome, evidence, hasOutcomeEvidence, hasReconciliationEvidence)
            select (phase, outcome, evidence, hasOutcomeEvidence, hasReconciliationEvidence));
        HashSet<(GovernedLoopEffectPhase, GovernedLoopEffectOutcome, GovernedLoopEffectEvidenceStatus, bool, bool)> expectedEffects =
        [
            (GovernedLoopEffectPhase.IntentPrepared, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Pending, false, false),
            (GovernedLoopEffectPhase.IntentPrepared, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Complete, false, false),
            (GovernedLoopEffectPhase.DispatchNotStarted, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Complete, false, false),
            (GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, false, false),
            (GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, false, false),
            (GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, true, false),
            (GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Incomplete, true, false),
            (GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Failed, GovernedLoopEffectEvidenceStatus.Complete, true, false),
            (GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Failed, GovernedLoopEffectEvidenceStatus.Incomplete, true, false),
            (GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Conflicted, GovernedLoopEffectEvidenceStatus.Conflicting, true, false),
            (GovernedLoopEffectPhase.Committed, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, true, false),
            (GovernedLoopEffectPhase.Committed, GovernedLoopEffectOutcome.Failed, GovernedLoopEffectEvidenceStatus.Complete, true, false),
            (GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, false, false),
            (GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Conflicting, false, false),
            (GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Incomplete, true, false),
            (GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.Failed, GovernedLoopEffectEvidenceStatus.Incomplete, true, false),
            (GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.Conflicted, GovernedLoopEffectEvidenceStatus.Incomplete, true, false),
            (GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.Conflicted, GovernedLoopEffectEvidenceStatus.Conflicting, true, false),
            (GovernedLoopEffectPhase.Reconciled, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Complete, false, true),
            (GovernedLoopEffectPhase.Reconciled, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, true, true),
            (GovernedLoopEffectPhase.Reconciled, GovernedLoopEffectOutcome.Failed, GovernedLoopEffectEvidenceStatus.Complete, true, true),
            (GovernedLoopEffectPhase.Reconciled, GovernedLoopEffectOutcome.Conflicted, GovernedLoopEffectEvidenceStatus.Complete, true, true)
        ];
        AssertSetEqual(expectedEffects, actualEffects);

        var actualProjections = new HashSet<(GovernedLoopProjectionClass Class, GovernedLoopProjectionStatus Status, bool HasExpectedVersion, bool HasCommittedVersion, bool HasReconciliationEvidence)>(
            from projectionClass in Enum.GetValues<GovernedLoopProjectionClass>()
            from status in Enum.GetValues<GovernedLoopProjectionStatus>()
            from hasExpectedVersion in new[] { false, true }
            from hasCommittedVersion in new[] { false, true }
            from hasReconciliationEvidence in new[] { false, true }
            where GovernedLoopExecutionStateMatrix.IsProjectionStateValid(projectionClass, status, hasExpectedVersion, hasCommittedVersion, hasReconciliationEvidence)
            select (projectionClass, status, hasExpectedVersion, hasCommittedVersion, hasReconciliationEvidence));
        HashSet<(GovernedLoopProjectionClass, GovernedLoopProjectionStatus, bool, bool, bool)> expectedProjections =
        [
            (GovernedLoopProjectionClass.LocalRuntime, GovernedLoopProjectionStatus.Pending, false, false, false),
            (GovernedLoopProjectionClass.LocalRuntime, GovernedLoopProjectionStatus.Committed, false, false, false),
            (GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Pending, true, false, false),
            (GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Committed, true, true, false),
            (GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Conflict, true, false, false),
            (GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.ReconciliationRequired, true, false, false),
            (GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Reconciled, true, false, true),
            (GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Reconciled, true, true, true),
            (GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Pending, false, false, false),
            (GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Pending, true, false, false),
            (GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Committed, false, true, false),
            (GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Committed, true, true, false),
            (GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Conflict, true, false, false),
            (GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.ReconciliationRequired, true, false, false),
            (GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Reconciled, true, false, true),
            (GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Reconciled, true, true, true)
        ];
        AssertSetEqual(expectedProjections, actualProjections);
    }

    [Fact]
    public void Frontier_shape_matrix_distinguishes_each_aggregate_posture()
    {
        var ready = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Ready);
        var running = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Running);
        var waiting = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Waiting);
        var review = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.ReviewBlocked);
        var completed = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Completed);
        var skipped = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Skipped, outcomeEvidenceId: "skip-evidence");
        var failed = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Failed);

        Assert.True(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.Active, [ready]));
        Assert.True(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.Active, [running]));
        Assert.True(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.Waiting, [waiting]));
        Assert.True(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.ReviewBlocked, [review]));
        Assert.True(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.Completed, [completed, skipped]));
        Assert.True(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.Failed, [failed, ready]));
        Assert.True(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.Cancelled, [running]));
        Assert.False(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.Active, [waiting]));
        Assert.False(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.Waiting, [waiting, ready]));
        Assert.False(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.ReviewBlocked, [review, running]));
        Assert.False(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.Completed, [completed, ready]));
        Assert.False(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.Failed, [failed, waiting]));
    }

    [Fact]
    public void Transition_matrices_match_the_complete_schema_one_truth_sets()
    {
        AssertAllowedTargets(GovernedLoopRunStatus.Unknown, [], GovernedLoopExecutionStateMatrix.IsRunTransitionAllowed);
        AssertAllowedTargets(GovernedLoopRunStatus.Admitted, [GovernedLoopRunStatus.Admitted, GovernedLoopRunStatus.Running, GovernedLoopRunStatus.Waiting, GovernedLoopRunStatus.PauseRequested, GovernedLoopRunStatus.Paused, GovernedLoopRunStatus.CancelRequested, GovernedLoopRunStatus.Failed, GovernedLoopRunStatus.Cancelled, GovernedLoopRunStatus.NeedsReview], GovernedLoopExecutionStateMatrix.IsRunTransitionAllowed);
        AssertAllowedTargets(GovernedLoopRunStatus.Running, [GovernedLoopRunStatus.Running, GovernedLoopRunStatus.Waiting, GovernedLoopRunStatus.PauseRequested, GovernedLoopRunStatus.Paused, GovernedLoopRunStatus.CancelRequested, GovernedLoopRunStatus.Completed, GovernedLoopRunStatus.Failed, GovernedLoopRunStatus.NeedsReview], GovernedLoopExecutionStateMatrix.IsRunTransitionAllowed);
        AssertAllowedTargets(GovernedLoopRunStatus.Waiting, [GovernedLoopRunStatus.Running, GovernedLoopRunStatus.Waiting, GovernedLoopRunStatus.PauseRequested, GovernedLoopRunStatus.Paused, GovernedLoopRunStatus.CancelRequested, GovernedLoopRunStatus.Failed, GovernedLoopRunStatus.NeedsReview], GovernedLoopExecutionStateMatrix.IsRunTransitionAllowed);
        AssertAllowedTargets(GovernedLoopRunStatus.PauseRequested, [GovernedLoopRunStatus.PauseRequested, GovernedLoopRunStatus.Paused, GovernedLoopRunStatus.CancelRequested, GovernedLoopRunStatus.Completed, GovernedLoopRunStatus.Failed, GovernedLoopRunStatus.NeedsReview], GovernedLoopExecutionStateMatrix.IsRunTransitionAllowed);
        AssertAllowedTargets(GovernedLoopRunStatus.Paused, [GovernedLoopRunStatus.Running, GovernedLoopRunStatus.Waiting, GovernedLoopRunStatus.Paused, GovernedLoopRunStatus.CancelRequested, GovernedLoopRunStatus.Failed, GovernedLoopRunStatus.Cancelled, GovernedLoopRunStatus.NeedsReview], GovernedLoopExecutionStateMatrix.IsRunTransitionAllowed);
        AssertAllowedTargets(GovernedLoopRunStatus.CancelRequested, [GovernedLoopRunStatus.CancelRequested, GovernedLoopRunStatus.Failed, GovernedLoopRunStatus.Cancelled, GovernedLoopRunStatus.NeedsReview], GovernedLoopExecutionStateMatrix.IsRunTransitionAllowed);
        AssertAllowedTargets(GovernedLoopRunStatus.Completed, [], GovernedLoopExecutionStateMatrix.IsRunTransitionAllowed);
        AssertAllowedTargets(GovernedLoopRunStatus.Failed, [], GovernedLoopExecutionStateMatrix.IsRunTransitionAllowed);
        AssertAllowedTargets(GovernedLoopRunStatus.Cancelled, [], GovernedLoopExecutionStateMatrix.IsRunTransitionAllowed);
        AssertAllowedTargets(GovernedLoopRunStatus.NeedsReview, [], GovernedLoopExecutionStateMatrix.IsRunTransitionAllowed);

        AssertAllowedTargets(GovernedLoopNodeExecutionStatus.Unknown, [], GovernedLoopExecutionStateMatrix.IsNodeTransitionAllowed);
        AssertAllowedTargets(GovernedLoopNodeExecutionStatus.Ready, [GovernedLoopNodeExecutionStatus.Ready, GovernedLoopNodeExecutionStatus.Running, GovernedLoopNodeExecutionStatus.Skipped, GovernedLoopNodeExecutionStatus.Failed], GovernedLoopExecutionStateMatrix.IsNodeTransitionAllowed);
        AssertAllowedTargets(GovernedLoopNodeExecutionStatus.Running, [GovernedLoopNodeExecutionStatus.Running, GovernedLoopNodeExecutionStatus.Completed, GovernedLoopNodeExecutionStatus.Waiting, GovernedLoopNodeExecutionStatus.Failed, GovernedLoopNodeExecutionStatus.ReviewBlocked], GovernedLoopExecutionStateMatrix.IsNodeTransitionAllowed);
        AssertAllowedTargets(GovernedLoopNodeExecutionStatus.Completed, [GovernedLoopNodeExecutionStatus.Completed], GovernedLoopExecutionStateMatrix.IsNodeTransitionAllowed);
        AssertAllowedTargets(GovernedLoopNodeExecutionStatus.Skipped, [GovernedLoopNodeExecutionStatus.Skipped], GovernedLoopExecutionStateMatrix.IsNodeTransitionAllowed);
        AssertAllowedTargets(GovernedLoopNodeExecutionStatus.Waiting, [GovernedLoopNodeExecutionStatus.Running, GovernedLoopNodeExecutionStatus.Waiting, GovernedLoopNodeExecutionStatus.Failed, GovernedLoopNodeExecutionStatus.ReviewBlocked], GovernedLoopExecutionStateMatrix.IsNodeTransitionAllowed);
        AssertAllowedTargets(GovernedLoopNodeExecutionStatus.Failed, [GovernedLoopNodeExecutionStatus.Failed], GovernedLoopExecutionStateMatrix.IsNodeTransitionAllowed);
        AssertAllowedTargets(GovernedLoopNodeExecutionStatus.ReviewBlocked, [GovernedLoopNodeExecutionStatus.Running, GovernedLoopNodeExecutionStatus.Failed, GovernedLoopNodeExecutionStatus.ReviewBlocked], GovernedLoopExecutionStateMatrix.IsNodeTransitionAllowed);

        AssertAllowedTargets(GovernedLoopFrontierStatus.Unknown, [], GovernedLoopExecutionStateMatrix.IsFrontierTransitionAllowed);
        AssertAllowedTargets(GovernedLoopFrontierStatus.Active, [GovernedLoopFrontierStatus.Active, GovernedLoopFrontierStatus.Waiting, GovernedLoopFrontierStatus.ReviewBlocked, GovernedLoopFrontierStatus.Completed, GovernedLoopFrontierStatus.Failed, GovernedLoopFrontierStatus.Cancelled], GovernedLoopExecutionStateMatrix.IsFrontierTransitionAllowed);
        AssertAllowedTargets(GovernedLoopFrontierStatus.Waiting, [GovernedLoopFrontierStatus.Active, GovernedLoopFrontierStatus.Waiting, GovernedLoopFrontierStatus.ReviewBlocked, GovernedLoopFrontierStatus.Failed, GovernedLoopFrontierStatus.Cancelled], GovernedLoopExecutionStateMatrix.IsFrontierTransitionAllowed);
        AssertAllowedTargets(GovernedLoopFrontierStatus.ReviewBlocked, [GovernedLoopFrontierStatus.Active, GovernedLoopFrontierStatus.Waiting, GovernedLoopFrontierStatus.ReviewBlocked, GovernedLoopFrontierStatus.Failed, GovernedLoopFrontierStatus.Cancelled], GovernedLoopExecutionStateMatrix.IsFrontierTransitionAllowed);
        AssertAllowedTargets(GovernedLoopFrontierStatus.Completed, [], GovernedLoopExecutionStateMatrix.IsFrontierTransitionAllowed);
        AssertAllowedTargets(GovernedLoopFrontierStatus.Failed, [], GovernedLoopExecutionStateMatrix.IsFrontierTransitionAllowed);
        AssertAllowedTargets(GovernedLoopFrontierStatus.Cancelled, [], GovernedLoopExecutionStateMatrix.IsFrontierTransitionAllowed);

        AssertAllowedTargets(GovernedLoopEffectPhase.Unknown, [], GovernedLoopExecutionStateMatrix.IsEffectTransitionAllowed);
        AssertAllowedTargets(GovernedLoopEffectPhase.IntentPrepared, [GovernedLoopEffectPhase.IntentPrepared, GovernedLoopEffectPhase.DispatchNotStarted, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectPhase.OutcomeObserved], GovernedLoopExecutionStateMatrix.IsEffectTransitionAllowed);
        AssertAllowedTargets(GovernedLoopEffectPhase.DispatchNotStarted, [GovernedLoopEffectPhase.DispatchNotStarted, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectPhase.OutcomeObserved], GovernedLoopExecutionStateMatrix.IsEffectTransitionAllowed);
        AssertAllowedTargets(GovernedLoopEffectPhase.DispatchBoundaryReached, [GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectPhase.ReconciliationRequired], GovernedLoopExecutionStateMatrix.IsEffectTransitionAllowed);
        AssertAllowedTargets(GovernedLoopEffectPhase.OutcomeObserved, [GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectPhase.Committed, GovernedLoopEffectPhase.ReconciliationRequired], GovernedLoopExecutionStateMatrix.IsEffectTransitionAllowed);
        AssertAllowedTargets(GovernedLoopEffectPhase.Committed, [GovernedLoopEffectPhase.Committed], GovernedLoopExecutionStateMatrix.IsEffectTransitionAllowed);
        AssertAllowedTargets(GovernedLoopEffectPhase.ReconciliationRequired, [GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectPhase.Reconciled], GovernedLoopExecutionStateMatrix.IsEffectTransitionAllowed);
        AssertAllowedTargets(GovernedLoopEffectPhase.Reconciled, [GovernedLoopEffectPhase.Reconciled], GovernedLoopExecutionStateMatrix.IsEffectTransitionAllowed);

        AssertAllowedTargets(GovernedLoopProjectionStatus.Unknown, [], GovernedLoopExecutionStateMatrix.IsProjectionTransitionAllowed);
        AssertAllowedTargets(GovernedLoopProjectionStatus.Pending, [GovernedLoopProjectionStatus.Pending, GovernedLoopProjectionStatus.Committed, GovernedLoopProjectionStatus.Conflict, GovernedLoopProjectionStatus.ReconciliationRequired], GovernedLoopExecutionStateMatrix.IsProjectionTransitionAllowed);
        AssertAllowedTargets(GovernedLoopProjectionStatus.Committed, [GovernedLoopProjectionStatus.Committed], GovernedLoopExecutionStateMatrix.IsProjectionTransitionAllowed);
        AssertAllowedTargets(GovernedLoopProjectionStatus.Conflict, [GovernedLoopProjectionStatus.Conflict, GovernedLoopProjectionStatus.ReconciliationRequired], GovernedLoopExecutionStateMatrix.IsProjectionTransitionAllowed);
        AssertAllowedTargets(GovernedLoopProjectionStatus.ReconciliationRequired, [GovernedLoopProjectionStatus.ReconciliationRequired, GovernedLoopProjectionStatus.Reconciled], GovernedLoopExecutionStateMatrix.IsProjectionTransitionAllowed);
        AssertAllowedTargets(GovernedLoopProjectionStatus.Reconciled, [GovernedLoopProjectionStatus.Reconciled], GovernedLoopExecutionStateMatrix.IsProjectionTransitionAllowed);

        Assert.False(GovernedLoopExecutionStateMatrix.IsEffectTransitionAllowed((GovernedLoopEffectPhase)99, GovernedLoopEffectPhase.IntentPrepared));
        Assert.False(GovernedLoopExecutionStateMatrix.IsProjectionTransitionAllowed((GovernedLoopProjectionStatus)99, GovernedLoopProjectionStatus.Pending));
    }

    [Fact]
    public void Node_evidence_transition_matrix_has_no_implicit_retry_edge()
    {
        var ready = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Ready);
        var running = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Running);
        var failed = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Failed);
        var skipped = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Skipped);
        var changedNode = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Running, "other");
        var changedEdges = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Running, incomingEdgeIds: ["other-edge"]);

        Assert.True(GovernedLoopExecutionStateMatrix.IsNodeEvidenceTransitionAllowed(ready, running));
        Assert.True(GovernedLoopExecutionStateMatrix.IsNodeEvidenceTransitionAllowed(ready, skipped));
        Assert.True(GovernedLoopExecutionStateMatrix.IsNodeEvidenceTransitionAllowed(ready, failed));
        Assert.False(GovernedLoopExecutionStateMatrix.IsNodeEvidenceTransitionAllowed(failed, running));
        Assert.False(GovernedLoopExecutionStateMatrix.IsNodeEvidenceTransitionAllowed(null, running));
        Assert.False(GovernedLoopExecutionStateMatrix.IsNodeEvidenceTransitionAllowed(running, changedNode));
        Assert.False(GovernedLoopExecutionStateMatrix.IsNodeEvidenceTransitionAllowed(running, changedEdges));
    }

    [Fact]
    public void Dispatch_eligibility_is_limited_to_pre_boundary_phases()
    {
        var effects = new Dictionary<GovernedLoopEffectPhase, GovernedLoopEffectPayload>
        {
            [GovernedLoopEffectPhase.IntentPrepared] = GovernedLoopExecutionTestFixture.Effect(),
            [GovernedLoopEffectPhase.DispatchNotStarted] = GovernedLoopExecutionTestFixture.Effect(GovernedLoopEffectPhase.DispatchNotStarted, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Complete),
            [GovernedLoopEffectPhase.DispatchBoundaryReached] = GovernedLoopExecutionTestFixture.Effect(GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete),
            [GovernedLoopEffectPhase.OutcomeObserved] = GovernedLoopExecutionTestFixture.Effect(GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, outcomeEvidenceId: "outcome"),
            [GovernedLoopEffectPhase.Committed] = GovernedLoopExecutionTestFixture.Effect(GovernedLoopEffectPhase.Committed, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, outcomeEvidenceId: "outcome"),
            [GovernedLoopEffectPhase.ReconciliationRequired] = GovernedLoopExecutionTestFixture.Effect(GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete),
            [GovernedLoopEffectPhase.Reconciled] = GovernedLoopExecutionTestFixture.Effect(GovernedLoopEffectPhase.Reconciled, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Complete, reconciliationEvidenceId: "disposition")
        };

        Assert.Equal(Enum.GetValues<GovernedLoopEffectPhase>().Length - 1, effects.Count);
        foreach (var (phase, effect) in effects)
        {
            Assert.Equal(
                phase is GovernedLoopEffectPhase.IntentPrepared or GovernedLoopEffectPhase.DispatchNotStarted,
                GovernedLoopExecutionStateMatrix.IsEffectDispatchEligible(effect));
        }

        Assert.False(GovernedLoopExecutionStateMatrix.IsEffectDispatchEligible(null));
    }

    private static void AssertSetEqual<T>(HashSet<T> expected, HashSet<T> actual)
    {
        Assert.Empty(expected.Except(actual));
        Assert.Empty(actual.Except(expected));
    }

    private static void AssertAllowedTargets<T>(T current, IReadOnlyCollection<T> expected, Func<T, T, bool> predicate)
        where T : struct, Enum
    {
        var actual = Enum.GetValues<T>().Where(next => predicate(current, next));
        Assert.Equal(expected.OrderBy(value => Convert.ToInt32(value)), actual.OrderBy(value => Convert.ToInt32(value)));
    }
}
