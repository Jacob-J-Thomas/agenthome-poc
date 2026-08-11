using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution;

public sealed class GovernedLoopExecutionTransitionTests
{
    [Theory]
    [InlineData(GovernedLoopEffectPhase.IntentPrepared, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Pending)]
    [InlineData(GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete)]
    public void Aggregate_transition_cannot_erase_retained_open_or_outcome_unknown_effect(
        GovernedLoopEffectPhase phase,
        GovernedLoopEffectOutcome outcome,
        GovernedLoopEffectEvidenceStatus evidenceStatus)
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var currentLifecycle = Lifecycle(binding, 1, GovernedLoopRunStatus.Running, GovernedLoopExecutionTestFixture.UpdatedAtUtc);
        var currentFrontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active);
        var effect = GovernedLoopEffectPosture.Create(binding, Effect(phase, outcome, evidenceStatus, null));
        var current = GovernedLoopExecutionEvidenceSet.Create(1, currentLifecycle, currentFrontier, [effect], []);
        var nextTime = GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1);
        var nextLifecycle = Lifecycle(binding, 2, GovernedLoopRunStatus.Completed, nextTime);
        var nextFrontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Completed, 2, updatedAtUtc: nextTime);
        var next = GovernedLoopExecutionEvidenceSet.Create(1, nextLifecycle, nextFrontier, [], []);

        var validation = GovernedLoopExecutionValidator.ValidateTransition(current, next);

        Assert.Contains(validation.Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.HistoricalEvidenceMissing && error.Path == "$transition.effects[0]");
    }

    [Fact]
    public void Aggregate_transition_cannot_erase_retained_projection_evidence()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var currentLifecycle = Lifecycle(binding, 1, GovernedLoopRunStatus.Running, GovernedLoopExecutionTestFixture.UpdatedAtUtc);
        var currentFrontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active);
        var projection = GovernedLoopProjectionPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Projection(sourceEvidenceId: binding.RunId, effectId: null));
        var current = GovernedLoopExecutionEvidenceSet.Create(1, currentLifecycle, currentFrontier, [], [projection]);
        var nextTime = GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1);
        var nextLifecycle = Lifecycle(binding, 2, GovernedLoopRunStatus.Completed, nextTime);
        var nextFrontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Completed, 2, updatedAtUtc: nextTime);
        var next = GovernedLoopExecutionEvidenceSet.Create(1, nextLifecycle, nextFrontier, [], []);

        var validation = GovernedLoopExecutionValidator.ValidateTransition(current, next);

        Assert.Contains(validation.Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.HistoricalEvidenceMissing && error.Path == "$transition.projections[0]");
    }

    [Fact]
    public void Aggregate_transition_accepts_safe_retained_successor_with_unchanged_terminal_planes()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = Lifecycle(binding, 1, GovernedLoopRunStatus.NeedsReview, GovernedLoopExecutionTestFixture.UpdatedAtUtc);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Completed);
        var required = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Incomplete, "outcome"));
        var current = GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, frontier, [required], []);
        var reconciled = GovernedLoopEffectPosture.Create(
            binding,
            Effect(
                GovernedLoopEffectPhase.Reconciled,
                GovernedLoopEffectOutcome.Succeeded,
                GovernedLoopEffectEvidenceStatus.Complete,
                "outcome",
                "disposition",
                GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1)));
        var next = GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, frontier, [reconciled], []);

        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(current, next).IsValid);
    }

    [Fact]
    public void Aggregate_transition_preserves_existing_items_while_allowing_new_canonical_effects_and_projections()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = Lifecycle(binding, 1, GovernedLoopRunStatus.Running, GovernedLoopExecutionTestFixture.UpdatedAtUtc);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active);
        var retained = GovernedLoopEffectPosture.Create(binding, GovernedLoopExecutionTestFixture.Effect(effectId: "a"));
        var current = GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, frontier, [retained], []);
        var added = GovernedLoopEffectPosture.Create(binding, GovernedLoopExecutionTestFixture.Effect(effectId: "b"));
        var projection = GovernedLoopProjectionPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Projection(projectionId: "b-view", sourceEvidenceId: "b", effectId: "b"));
        var next = GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, frontier, [retained, added], [projection]);

        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(current, next).IsValid);
        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(next, next).IsValid);
    }

    [Fact]
    public void Lifecycle_transition_requires_same_binding_contiguous_version_immutable_creation_and_legal_status()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var current = Lifecycle(binding, 1, GovernedLoopRunStatus.Running, GovernedLoopExecutionTestFixture.UpdatedAtUtc);
        var valid = Lifecycle(binding, 2, GovernedLoopRunStatus.Waiting, GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1));
        var wrongBinding = Lifecycle(GovernedLoopExecutionTestFixture.Binding(2), 2, GovernedLoopRunStatus.Waiting, GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1));
        var skippedVersion = Lifecycle(binding, 3, GovernedLoopRunStatus.Waiting, GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1));
        var changedCreation = GovernedLoopRunLifecycle.Create(binding, GovernedLoopRunLifecyclePayload.Create(1, 2, GovernedLoopRunStatus.Waiting, GovernedLoopExecutionTestFixture.CreatedAtUtc.AddSeconds(1), GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1), null));
        var regressedTime = Lifecycle(binding, 2, GovernedLoopRunStatus.Waiting, GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddSeconds(-1));

        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(current, valid).IsValid);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(current, wrongBinding).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.BindingMismatch);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(current, skippedVersion).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.InvalidSuccessorVersion);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(current, changedCreation).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(current, regressedTime).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition);
    }

    [Theory]
    [InlineData(GovernedLoopRunStatus.Completed)]
    [InlineData(GovernedLoopRunStatus.Failed)]
    [InlineData(GovernedLoopRunStatus.Cancelled)]
    [InlineData(GovernedLoopRunStatus.NeedsReview)]
    public void Terminal_lifecycle_cannot_advance_even_to_same_status(GovernedLoopRunStatus status)
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var current = Lifecycle(binding, 1, status, GovernedLoopExecutionTestFixture.UpdatedAtUtc);
        var next = Lifecycle(binding, 2, status, GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1));

        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(current, next).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition);
    }

    [Fact]
    public void Frontier_transition_preserves_nodes_edges_attempts_and_committed_outcomes()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var currentNode = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Running, incomingEdgeIds: ["edge-a"]);
        var current = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active, 1, [currentNode]);
        var waitingNode = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Waiting, incomingEdgeIds: ["edge-a"]);
        var valid = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Waiting, 2, [waitingNode], GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1));
        var changedEdges = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Waiting, 2, [GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Waiting, incomingEdgeIds: ["edge-b"])], GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1));
        var changedAttempt = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Waiting, 2, [GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Waiting, incomingEdgeIds: ["edge-a"], attempt: 2)], GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1));
        var missing = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active, 2, [GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Ready, "later")], GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1));

        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(current, valid).IsValid);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(current, changedEdges).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(current, changedAttempt).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(current, missing).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged);

        var completed = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Completed, outcomeEvidenceId: "outcome-a");
        var changedOutcome = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Completed, outcomeEvidenceId: "outcome-b");
        Assert.False(GovernedLoopExecutionStateMatrix.IsNodeEvidenceTransitionAllowed(completed, changedOutcome));
        Assert.False(GovernedLoopExecutionStateMatrix.IsNodeEvidenceTransitionAllowed(completed, GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Ready)));
    }

    [Fact]
    public void Frontier_transition_freezes_workspace_graph_layout_and_admission_coordinates()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var currentNode = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Running, incomingEdgeIds: ["edge-a"]);
        var current = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active, 1, [currentNode]);
        var waitingNode = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Waiting, incomingEdgeIds: ["edge-a"]);
        var valid = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Waiting, 2, [waitingNode], GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1));
        var payload = GovernedLoopFrontierPayload.Create(1, valid.Payload.FrontierVersion, valid.Payload.ConcurrencyCeiling, valid.Payload.Status, valid.Payload.Nodes, valid.Payload.UpdatedAtUtc, string.Empty);
        GovernedLoopFrontierPosture[] substitutions =
        [
            GovernedLoopFrontierPosture.Create(binding, "workspace-sha256:" + new string('9', 64), valid.GraphArtifactHash, valid.GraphLayoutHash, valid.AdmissionReceiptHash, payload),
            GovernedLoopFrontierPosture.Create(binding, valid.WorkspaceId, new string('9', 64), valid.GraphLayoutHash, valid.AdmissionReceiptHash, payload),
            GovernedLoopFrontierPosture.Create(binding, valid.WorkspaceId, valid.GraphArtifactHash, new string('9', 64), valid.AdmissionReceiptHash, payload),
            GovernedLoopFrontierPosture.Create(binding, valid.WorkspaceId, valid.GraphArtifactHash, valid.GraphLayoutHash, new string('9', 64), payload),
        ];

        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(current, valid).IsValid);
        Assert.All(substitutions, candidate => Assert.Contains(
            GovernedLoopExecutionValidator.ValidateTransition(current, candidate).Errors,
            error => error.Code == GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged));
    }

    [Theory]
    [InlineData(GovernedLoopFrontierStatus.Active, GovernedLoopNodeExecutionStatus.Running)]
    [InlineData(GovernedLoopFrontierStatus.Waiting, GovernedLoopNodeExecutionStatus.Waiting)]
    [InlineData(GovernedLoopFrontierStatus.ReviewBlocked, GovernedLoopNodeExecutionStatus.ReviewBlocked)]
    public void Cancellation_retains_each_nodes_last_committed_posture(GovernedLoopFrontierStatus currentStatus, GovernedLoopNodeExecutionStatus nodeStatus)
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var node = GovernedLoopExecutionTestFixture.Node(nodeStatus);
        var current = GovernedLoopExecutionTestFixture.Frontier(binding, currentStatus, 1, [node]);
        var cancelled = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Cancelled, 2, [node], GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1));

        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(current, cancelled).IsValid);
    }

    [Fact]
    public void Cancellation_cannot_rewrite_or_append_node_evidence()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var running = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Running, "a");
        var current = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active, 1, [running]);
        var fabricatedFailure = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Failed, "a");
        var rewritten = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Cancelled, 2, [fabricatedFailure], GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1));
        var appended = GovernedLoopExecutionTestFixture.Frontier(
            binding,
            GovernedLoopFrontierStatus.Cancelled,
            2,
            [running, GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Completed, "b", planOrdinal: 1)],
            GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1));

        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(current, rewritten).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(current, appended).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged);
    }

    [Theory]
    [InlineData(GovernedLoopFrontierStatus.Completed)]
    [InlineData(GovernedLoopFrontierStatus.Failed)]
    [InlineData(GovernedLoopFrontierStatus.Cancelled)]
    public void Terminal_frontier_cannot_advance_even_to_same_status(GovernedLoopFrontierStatus status)
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var node = status switch
        {
            GovernedLoopFrontierStatus.Completed => GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Completed),
            GovernedLoopFrontierStatus.Failed => GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Failed),
            _ => GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Running)
        };
        var current = GovernedLoopExecutionTestFixture.Frontier(binding, status, 1, [node]);
        var next = GovernedLoopExecutionTestFixture.Frontier(binding, status, 2, [node], GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1));

        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(current, next).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition);
    }

    [Fact]
    public void Effect_transition_preserves_conclusive_observations_and_terminal_postures()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var observed = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome-a"));
        var committed = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.Committed, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome-a", updatedAtUtc: GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1)));
        var changedOutcome = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.Committed, GovernedLoopEffectOutcome.Failed, GovernedLoopEffectEvidenceStatus.Complete, "outcome-b", updatedAtUtc: GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1)));
        var mutatedCommitted = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.Committed, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome-a", updatedAtUtc: GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(2)));

        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(observed, committed).IsValid);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(observed, changedOutcome).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(committed, mutatedCommitted).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition);
        Assert.False(GovernedLoopExecutionStateMatrix.IsEffectDispatchEligible(committed.Payload));
    }

    [Fact]
    public void Reconciliation_adds_disposition_without_erasing_prior_conflict_evidence()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var required = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.Conflicted, GovernedLoopEffectEvidenceStatus.Conflicting, "conflict-evidence"));
        var reconciled = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.Reconciled, GovernedLoopEffectOutcome.Conflicted, GovernedLoopEffectEvidenceStatus.Complete, "conflict-evidence", "disposition", GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1)));
        var erased = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.Reconciled, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Complete, null, "disposition", GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1)));

        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(required, reconciled).IsValid);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(required, erased).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition);
        Assert.False(GovernedLoopExecutionStateMatrix.IsEffectDispatchEligible(reconciled.Payload));
        Assert.Throws<ArgumentException>(() => Effect(GovernedLoopEffectPhase.Reconciled, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, null, "human-disposition"));
    }

    [Theory]
    [InlineData(GovernedLoopEffectOutcome.Succeeded)]
    [InlineData(GovernedLoopEffectOutcome.Failed)]
    public void Incomplete_conclusive_outcome_can_be_reconciled_without_changing_its_outcome_evidence(GovernedLoopEffectOutcome outcome)
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var observed = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.OutcomeObserved, outcome, GovernedLoopEffectEvidenceStatus.Incomplete, "outcome-a"));
        var required = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.ReconciliationRequired, outcome, GovernedLoopEffectEvidenceStatus.Incomplete, "outcome-a", updatedAtUtc: GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1)));
        var reconciled = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.Reconciled, outcome, GovernedLoopEffectEvidenceStatus.Complete, "outcome-a", "disposition", GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(2)));
        var changedOutcome = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.Reconciled, outcome == GovernedLoopEffectOutcome.Succeeded ? GovernedLoopEffectOutcome.Failed : GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome-b", "disposition", GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(2)));

        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(observed, required).IsValid);
        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(required, reconciled).IsValid);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(required, changedOutcome).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition);
    }

    [Fact]
    public void Unknown_boundary_cannot_fabricate_a_conclusive_reconciliation_requirement()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var boundary = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null));
        var fabricated = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Incomplete, "fabricated", updatedAtUtc: GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1)));

        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(boundary, fabricated).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition);
    }

    [Fact]
    public void Projection_transition_preserves_operation_precondition_and_committed_posture()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var pending = GovernedLoopProjectionPosture.Create(binding, Projection("operation", GovernedLoopProjectionStatus.Pending, "v1", null));
        var committed = GovernedLoopProjectionPosture.Create(binding, Projection("operation", GovernedLoopProjectionStatus.Committed, "v1", "v2", GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1)));
        var changedPrecondition = GovernedLoopProjectionPosture.Create(binding, Projection("operation", GovernedLoopProjectionStatus.Committed, "different", "v2", GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1)));
        var changedOperation = GovernedLoopProjectionPosture.Create(binding, Projection("new-operation", GovernedLoopProjectionStatus.Committed, "v1", "v2", GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1)));
        var mutatedCommitted = GovernedLoopProjectionPosture.Create(binding, Projection("operation", GovernedLoopProjectionStatus.Committed, "v1", "v2", GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(2)));

        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(pending, committed).IsValid);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(pending, changedPrecondition).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(pending, changedOperation).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(committed, mutatedCommitted).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition);
    }

    [Fact]
    public void Projection_reconciliation_requires_explicit_disposition_and_cannot_return_to_ordinary_sync_states()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var conflict = GovernedLoopProjectionPosture.Create(binding, Projection("operation", GovernedLoopProjectionStatus.Conflict, "v1", null));
        var required = GovernedLoopProjectionPosture.Create(binding, Projection("operation", GovernedLoopProjectionStatus.ReconciliationRequired, "v1", null, GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1)));
        var reconciled = GovernedLoopProjectionPosture.Create(binding, Projection("operation", GovernedLoopProjectionStatus.Reconciled, "v1", "v2", GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(2), "disposition"));
        var committed = GovernedLoopProjectionPosture.Create(binding, Projection("operation", GovernedLoopProjectionStatus.Committed, "v1", "v2", GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(2)));
        var pending = GovernedLoopProjectionPosture.Create(binding, Projection("operation", GovernedLoopProjectionStatus.Pending, "v1", null, GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(2)));
        var mutatedReconciled = GovernedLoopProjectionPosture.Create(binding, Projection("operation", GovernedLoopProjectionStatus.Reconciled, "v1", "v2", GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(3), "disposition"));

        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(conflict, required).IsValid);
        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(required, reconciled).IsValid);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(required, committed).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(required, pending).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateTransition(reconciled, mutatedReconciled).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition);
    }

    [Fact]
    public void Null_transition_contracts_return_bounded_errors()
    {
        Assert.False(GovernedLoopExecutionValidator.ValidateTransition((GovernedLoopExecutionEvidenceSet?)null, null).IsValid);
        Assert.False(GovernedLoopExecutionValidator.ValidateTransition((GovernedLoopRunLifecycle?)null, null).IsValid);
        Assert.False(GovernedLoopExecutionValidator.ValidateTransition((GovernedLoopFrontierPosture?)null, null).IsValid);
        Assert.False(GovernedLoopExecutionValidator.ValidateTransition((GovernedLoopEffectPosture?)null, null).IsValid);
        Assert.False(GovernedLoopExecutionValidator.ValidateTransition((GovernedLoopProjectionPosture?)null, null).IsValid);
    }

    private static GovernedLoopRunLifecycle Lifecycle(GovernedLoopExecutionBinding binding, long version, GovernedLoopRunStatus status, DateTimeOffset updatedAtUtc)
    {
        DateTimeOffset? terminal = GovernedLoopExecutionStateMatrix.IsTerminal(status) ? updatedAtUtc : null;
        return GovernedLoopRunLifecycle.Create(binding, GovernedLoopRunLifecyclePayload.Create(1, version, status, GovernedLoopExecutionTestFixture.CreatedAtUtc, updatedAtUtc, terminal));
    }

    private static GovernedLoopEffectPayload Effect(
        GovernedLoopEffectPhase phase,
        GovernedLoopEffectOutcome outcome,
        GovernedLoopEffectEvidenceStatus evidenceStatus,
        string? outcomeEvidenceId,
        string? reconciliationEvidenceId = null,
        DateTimeOffset? updatedAtUtc = null)
    {
        return GovernedLoopEffectPayload.Create(1, "effect", "operation", 1, GovernedLoopEffectOrigin.Provider, "infer", new string('a', 64), phase, outcome, evidenceStatus, outcomeEvidenceId, reconciliationEvidenceId, updatedAtUtc ?? GovernedLoopExecutionTestFixture.UpdatedAtUtc);
    }

    private static GovernedLoopProjectionPayload Projection(
        string operationId,
        GovernedLoopProjectionStatus status,
        string expectedVersion,
        string? committedVersion,
        DateTimeOffset? updatedAtUtc = null,
        string? reconciliationEvidenceId = null)
    {
        return GovernedLoopProjectionPayload.Create(1, "projection", operationId, GovernedLoopProjectionClass.DurableReadModel, status, "effect", "effect", expectedVersion, committedVersion, reconciliationEvidenceId, updatedAtUtc ?? GovernedLoopExecutionTestFixture.UpdatedAtUtc);
    }
}
