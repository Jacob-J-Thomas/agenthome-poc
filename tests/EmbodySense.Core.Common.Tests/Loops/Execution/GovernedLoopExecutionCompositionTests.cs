using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution;

public sealed class GovernedLoopExecutionCompositionTests
{
    [Fact]
    public void Every_child_plane_must_share_the_exact_binding()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var other = GovernedLoopExecutionTestFixture.Binding(2);
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Running);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(other, GovernedLoopFrontierStatus.Active);
        var effect = GovernedLoopEffectPosture.Create(other, GovernedLoopExecutionTestFixture.Effect());
        var projection = GovernedLoopProjectionPosture.Create(other, GovernedLoopExecutionTestFixture.Projection());

        var result = GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [effect], [projection]);

        Assert.Equal(3, result.Errors.Count(error => error.Code == GovernedLoopExecutionValidationErrorCode.BindingMismatch));
    }

    [Fact]
    public void Effects_and_projections_must_be_sorted_and_unique()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Running);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active);
        var effectA = GovernedLoopEffectPosture.Create(binding, GovernedLoopExecutionTestFixture.Effect(effectId: "a"));
        var effectB = GovernedLoopEffectPosture.Create(binding, GovernedLoopExecutionTestFixture.Effect(effectId: "b"));
        var projectionA = GovernedLoopProjectionPosture.Create(binding, GovernedLoopExecutionTestFixture.Projection(projectionId: "a", sourceEvidenceId: "a", effectId: "a"));
        var projectionB = GovernedLoopProjectionPosture.Create(binding, GovernedLoopExecutionTestFixture.Projection(projectionId: "b", sourceEvidenceId: "b", effectId: "b"));

        var result = GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [effectB, effectA], [projectionB, projectionA]);

        Assert.Equal(2, result.Errors.Count(error => error.Code == GovernedLoopExecutionValidationErrorCode.CollectionNotCanonical));
        Assert.Throws<ArgumentException>(() => GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, frontier, [effectA, effectA], []));
    }

    [Fact]
    public void Effect_origins_and_projection_sources_resolve_against_retained_evidence()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Running);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active);
        var missingNode = GovernedLoopEffectPosture.Create(binding, GovernedLoopEffectPayload.Create(1, "effect", "operation", 1, GovernedLoopEffectOrigin.Provider, "other-node", new string('a', 64), GovernedLoopEffectPhase.IntentPrepared, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Pending, null, null, GovernedLoopExecutionTestFixture.UpdatedAtUtc));
        var effect = GovernedLoopEffectPosture.Create(binding, GovernedLoopExecutionTestFixture.Effect());
        var missingSource = GovernedLoopProjectionPosture.Create(binding, GovernedLoopExecutionTestFixture.Projection(sourceEvidenceId: "missing", effectId: null));
        var mismatchedEffect = GovernedLoopProjectionPosture.Create(binding, GovernedLoopExecutionTestFixture.Projection(sourceEvidenceId: "provider-effect", effectId: "different"));

        Assert.Contains(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [missingNode], []).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.EffectOriginNodeMissing);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [effect], [missingSource]).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.ProjectionSourceMissing);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [effect], [mismatchedEffect]).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.ProjectionEffectMismatch);
    }

    [Theory]
    [InlineData(GovernedLoopEffectOrigin.Provider, GovernedLoopNodeExecutionStatus.Ready)]
    [InlineData(GovernedLoopEffectOrigin.Provider, GovernedLoopNodeExecutionStatus.Skipped)]
    [InlineData(GovernedLoopEffectOrigin.Actuator, GovernedLoopNodeExecutionStatus.Ready)]
    [InlineData(GovernedLoopEffectOrigin.Actuator, GovernedLoopNodeExecutionStatus.Skipped)]
    [InlineData(GovernedLoopEffectOrigin.MemoryMutation, GovernedLoopNodeExecutionStatus.Ready)]
    [InlineData(GovernedLoopEffectOrigin.MemoryMutation, GovernedLoopNodeExecutionStatus.Skipped)]
    [InlineData(GovernedLoopEffectOrigin.Publication, GovernedLoopNodeExecutionStatus.Ready)]
    [InlineData(GovernedLoopEffectOrigin.Publication, GovernedLoopNodeExecutionStatus.Skipped)]
    public void Node_attributed_effects_require_an_origin_node_that_entered_execution(
        GovernedLoopEffectOrigin origin,
        GovernedLoopNodeExecutionStatus nodeStatus)
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycleStatus = nodeStatus == GovernedLoopNodeExecutionStatus.Ready ? GovernedLoopRunStatus.Running : GovernedLoopRunStatus.Cancelled;
        var frontierStatus = nodeStatus == GovernedLoopNodeExecutionStatus.Ready ? GovernedLoopFrontierStatus.Active : GovernedLoopFrontierStatus.Cancelled;
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, lifecycleStatus);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, frontierStatus, nodes: [GovernedLoopExecutionTestFixture.Node(nodeStatus)]);
        var effect = GovernedLoopEffectPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Effect(
                GovernedLoopEffectPhase.Committed,
                GovernedLoopEffectOutcome.Succeeded,
                GovernedLoopEffectEvidenceStatus.Complete,
                outcomeEvidenceId: "effect-outcome",
                origin: origin));

        var error = Assert.Single(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [effect], []).Errors);

        Assert.Equal(GovernedLoopExecutionValidationErrorCode.EffectOriginNodeNotExecutable, error.Code);
        Assert.Equal("$.effects[0].payload.originNodeId", error.Path);
        Assert.Equal("Governed-loop execution contract rejected: a node-attributed effect originates from a node posture that cannot have dispatched work.", error.Message);
    }

    [Fact]
    public void Run_scoped_effects_need_no_node_posture_and_effect_generation_is_independent_from_node_attempt()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Running);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(
            binding,
            GovernedLoopFrontierStatus.Active,
            nodes: [GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Running, attempt: 2)]);
        var runScoped = GovernedLoopEffectPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Effect(
                origin: GovernedLoopEffectOrigin.Publication,
                originNodeId: null,
                effectGeneration: 7));

        Assert.True(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [runScoped], []).IsValid);
    }

    [Fact]
    public void Effect_ids_cannot_duplicate_one_operation_generation_while_later_generations_remain_distinct()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Running);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active);
        var committed = GovernedLoopEffectPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Effect(
                GovernedLoopEffectPhase.Committed,
                GovernedLoopEffectOutcome.Succeeded,
                GovernedLoopEffectEvidenceStatus.Complete,
                effectId: "a",
                outcomeEvidenceId: "effect-outcome",
                operationId: "same-operation"));
        var duplicate = GovernedLoopEffectPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Effect(effectId: "b", operationId: "same-operation"));
        var nextGeneration = GovernedLoopEffectPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Effect(effectId: "b", operationId: "same-operation", effectGeneration: 2));

        var duplicateResult = GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [committed, duplicate], []);

        Assert.Contains(duplicateResult.Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.EffectOperationGenerationNotUnique);
        Assert.True(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [committed, nextGeneration], []).IsValid);
    }

    [Fact]
    public void Pending_projection_source_is_required_and_aggregate_collections_are_defensive()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Running);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active);
        var effect = GovernedLoopEffectPosture.Create(binding, GovernedLoopExecutionTestFixture.Effect());
        var projection = GovernedLoopProjectionPosture.Create(binding, GovernedLoopExecutionTestFixture.Projection());
        var effects = new[] { effect };
        var projections = new[] { projection };

        var evidence = GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, frontier, effects, projections);
        effects[0] = GovernedLoopEffectPosture.Create(binding, GovernedLoopExecutionTestFixture.Effect(effectId: "changed"));
        projections[0] = GovernedLoopProjectionPosture.Create(binding, GovernedLoopExecutionTestFixture.Projection(projectionId: "changed"));

        Assert.Equal("provider-effect", evidence.Effects[0].Payload.EffectId);
        Assert.Equal("run-view", evidence.Projections[0].Payload.ProjectionId);
    }

    [Fact]
    public void Run_projection_can_source_the_bound_lifecycle_and_frontier_before_any_effect_exists()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Running);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active);
        var projection = GovernedLoopProjectionPosture.Create(binding, GovernedLoopExecutionTestFixture.Projection(sourceEvidenceId: binding.RunId, effectId: null));

        Assert.True(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [], [projection]).IsValid);
    }

    [Fact]
    public void Evidence_timestamps_must_lie_inside_the_lifecycle_interval()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Running);
        var tooLate = GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active, updatedAtUtc: tooLate);
        var effect = GovernedLoopEffectPosture.Create(binding, GovernedLoopExecutionTestFixture.Effect(updatedAtUtc: tooLate));
        var projection = GovernedLoopProjectionPosture.Create(binding, GovernedLoopExecutionTestFixture.Projection(updatedAtUtc: tooLate));

        var result = GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [effect], [projection]);

        Assert.Equal(3, result.Errors.Count(error => error.Code == GovernedLoopExecutionValidationErrorCode.TimestampOutsideLifecycle));
    }

    [Fact]
    public void Terminal_lifecycle_time_remains_immutable_while_later_reconciliation_evidence_composes()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.NeedsReview);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.ReviewBlocked);
        var reconciled = GovernedLoopEffectPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Effect(
                GovernedLoopEffectPhase.Reconciled,
                GovernedLoopEffectOutcome.OutcomeUnknown,
                GovernedLoopEffectEvidenceStatus.Complete,
                reconciliationEvidenceId: "operator-disposition",
                updatedAtUtc: lifecycle.Payload.UpdatedAtUtc.AddMinutes(1)));

        var result = GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [reconciled], []);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(GovernedLoopExecutionTestFixture.UpdatedAtUtc, lifecycle.Payload.UpdatedAtUtc);
    }

    [Fact]
    public void Terminal_frontier_timestamp_cannot_advance_past_the_immutable_lifecycle_terminal()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.NeedsReview);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(
            binding,
            GovernedLoopFrontierStatus.ReviewBlocked,
            updatedAtUtc: lifecycle.Payload.UpdatedAtUtc.AddMinutes(1));
        var ambiguity = GovernedLoopEffectPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Effect(
                GovernedLoopEffectPhase.DispatchBoundaryReached,
                GovernedLoopEffectOutcome.OutcomeUnknown,
                GovernedLoopEffectEvidenceStatus.Incomplete));

        var result = GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [ambiguity], []);

        var error = Assert.Single(result.Errors);
        Assert.Equal(GovernedLoopExecutionValidationErrorCode.TimestampOutsideLifecycle, error.Code);
        Assert.Equal("$.frontier.payload.updatedAtUtc", error.Path);
    }

    [Fact]
    public void Terminal_lifecycle_accepts_later_append_only_projection_evidence()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.NeedsReview);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.ReviewBlocked);
        var projection = GovernedLoopProjectionPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Projection(
                GovernedLoopProjectionClass.Surface,
                GovernedLoopProjectionStatus.Conflict,
                sourceEvidenceId: binding.RunId,
                effectId: null,
                expectedVersion: "etag",
                updatedAtUtc: lifecycle.Payload.UpdatedAtUtc.AddMinutes(1)));

        Assert.True(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [], [projection]).IsValid);
    }

    [Fact]
    public void Human_review_gate_is_waiting_while_ambiguity_review_is_terminal_needs_review()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var gateLifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Waiting);
        var reviewFrontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.ReviewBlocked);
        var ambiguityLifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.NeedsReview);
        var ambiguity = GovernedLoopEffectPosture.Create(binding, GovernedLoopExecutionTestFixture.Effect(GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete));

        Assert.True(GovernedLoopExecutionValidator.ValidateComposition(1, gateLifecycle, reviewFrontier, [], []).IsValid);
        Assert.True(GovernedLoopExecutionValidator.ValidateComposition(1, ambiguityLifecycle, reviewFrontier, [ambiguity], []).IsValid);
    }

    [Fact]
    public void Needs_review_accepts_an_immutable_terminal_frontier_with_retained_ambiguity()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.NeedsReview);
        var completed = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Completed);
        var active = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active);
        var waiting = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Waiting);
        var ambiguity = GovernedLoopEffectPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Effect(
                GovernedLoopEffectPhase.ReconciliationRequired,
                GovernedLoopEffectOutcome.Succeeded,
                GovernedLoopEffectEvidenceStatus.Incomplete,
                outcomeEvidenceId: "effect-outcome"));

        Assert.True(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, completed, [ambiguity], []).IsValid);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, active, [ambiguity], []).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.LifecycleFrontierMismatch);
        Assert.Contains(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, waiting, [ambiguity], []).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.LifecycleFrontierMismatch);
    }

    [Fact]
    public void Paused_active_frontier_requires_a_safe_point_without_running_nodes()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Paused);
        var running = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active);
        var ready = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active, nodes: [GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Ready)]);

        Assert.Contains(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, running, [], []).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.LifecycleFrontierMismatch);
        Assert.True(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, ready, [], []).IsValid);
    }

    [Fact]
    public void Conclusive_terminals_require_resolved_effects_and_committed_projections()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Completed);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Completed);
        var effect = GovernedLoopEffectPosture.Create(binding, GovernedLoopExecutionTestFixture.Effect(GovernedLoopEffectPhase.Committed, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, outcomeEvidenceId: "effect-outcome"));
        var pending = GovernedLoopProjectionPosture.Create(binding, GovernedLoopExecutionTestFixture.Projection(sourceEvidenceId: "effect-outcome"));
        var committed = GovernedLoopProjectionPosture.Create(binding, GovernedLoopExecutionTestFixture.Projection(GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Committed, sourceEvidenceId: "effect-outcome", committedVersion: "etag"));

        Assert.Contains(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [effect], [pending]).Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.TerminalEvidenceUnresolved);
        Assert.True(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [effect], [committed]).IsValid);
    }

    [Theory]
    [MemberData(nameof(UnresolvedEffectTerminalCases))]
    public void Every_conclusive_terminal_rejects_each_legal_unresolved_effect_posture(
        GovernedLoopRunStatus lifecycleStatus,
        GovernedLoopEffectPhase phase,
        GovernedLoopEffectOutcome outcome,
        GovernedLoopEffectEvidenceStatus evidenceStatus,
        string? outcomeEvidenceId)
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, lifecycleStatus);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, FrontierFor(lifecycleStatus));
        var effect = GovernedLoopEffectPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Effect(
                phase,
                outcome,
                evidenceStatus,
                outcomeEvidenceId: outcomeEvidenceId,
                origin: GovernedLoopEffectOrigin.Publication,
                originNodeId: null));

        var result = GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [effect], []);

        Assert.Contains(result.Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.TerminalEvidenceUnresolved);
    }

    [Theory]
    [MemberData(nameof(NonCommittedProjectionTerminalCases))]
    public void Every_conclusive_terminal_rejects_each_legal_noncommitted_projection_posture(
        GovernedLoopRunStatus lifecycleStatus,
        GovernedLoopProjectionClass projectionClass,
        GovernedLoopProjectionStatus projectionStatus,
        string? expectedVersion)
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, lifecycleStatus);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, FrontierFor(lifecycleStatus));
        var projection = GovernedLoopProjectionPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Projection(
                projectionClass,
                projectionStatus,
                sourceEvidenceId: binding.RunId,
                effectId: null,
                expectedVersion: expectedVersion));

        var result = GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [], [projection]);

        Assert.Contains(result.Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.TerminalEvidenceUnresolved);
    }

    [Fact]
    public void Reconciled_projection_is_resolved_for_conclusive_terminal_and_retains_prior_ambiguity_for_needs_review()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var completed = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Completed);
        var needsReview = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.NeedsReview);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Completed);
        var reconciled = GovernedLoopProjectionPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Projection(
                GovernedLoopProjectionClass.Surface,
                GovernedLoopProjectionStatus.Reconciled,
                sourceEvidenceId: binding.RunId,
                effectId: null,
                expectedVersion: "etag",
                reconciliationEvidenceId: "operator-disposition"));

        Assert.True(GovernedLoopExecutionValidator.ValidateComposition(1, completed, frontier, [], [reconciled]).IsValid);
        Assert.True(GovernedLoopExecutionValidator.ValidateComposition(1, needsReview, frontier, [], [reconciled]).IsValid);
    }

    [Theory]
    [InlineData(GovernedLoopEffectOutcome.Succeeded)]
    [InlineData(GovernedLoopEffectOutcome.Failed)]
    public void Reconciled_conclusive_effect_retains_the_historical_ambiguity_required_by_immutable_needs_review(GovernedLoopEffectOutcome outcome)
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.NeedsReview);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Completed);
        var observed = GovernedLoopEffectPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Effect(
                GovernedLoopEffectPhase.OutcomeObserved,
                outcome,
                GovernedLoopEffectEvidenceStatus.Incomplete,
                outcomeEvidenceId: "effect-outcome"));
        var required = GovernedLoopEffectPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Effect(
                GovernedLoopEffectPhase.ReconciliationRequired,
                outcome,
                GovernedLoopEffectEvidenceStatus.Incomplete,
                outcomeEvidenceId: "effect-outcome",
                updatedAtUtc: lifecycle.Payload.UpdatedAtUtc.AddMinutes(1)));
        var reconciled = GovernedLoopEffectPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Effect(
                GovernedLoopEffectPhase.Reconciled,
                outcome,
                GovernedLoopEffectEvidenceStatus.Complete,
                outcomeEvidenceId: "effect-outcome",
                reconciliationEvidenceId: "operator-disposition",
                updatedAtUtc: lifecycle.Payload.UpdatedAtUtc.AddMinutes(2)));

        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(observed, required).IsValid);
        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(required, reconciled).IsValid);
        Assert.True(GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [reconciled], []).IsValid);
    }

    [Fact]
    public void Null_and_unsupported_composition_inputs_return_value_free_bounded_errors()
    {
        var result = GovernedLoopExecutionValidator.ValidateComposition(2, null, null, null, null);

        Assert.False(result.IsValid);
        Assert.All(result.Errors, error =>
        {
            Assert.DoesNotContain("secret-value", error.Message, StringComparison.Ordinal);
            Assert.InRange(error.Path.Length, 1, GovernedLoopExecutionLimits.MaxErrorPathCharacters);
        });
        Assert.False(GovernedLoopExecutionValidator.Validate((GovernedLoopExecutionBinding?)null).IsValid);
        Assert.False(GovernedLoopExecutionValidator.Validate((GovernedLoopRunLifecyclePayload?)null).IsValid);
        Assert.False(GovernedLoopExecutionValidator.Validate((GovernedLoopFrontierPayload?)null).IsValid);
        Assert.False(GovernedLoopExecutionValidator.Validate((GovernedLoopEffectPayload?)null).IsValid);
        Assert.False(GovernedLoopExecutionValidator.Validate((GovernedLoopProjectionPayload?)null).IsValid);
        Assert.False(GovernedLoopExecutionValidator.Validate((GovernedLoopExecutionEvidenceSet?)null).IsValid);

        var first = result.Errors[0];
        var same = result.Errors.Single(error => error.Code == first.Code && error.Path == first.Path);
        Assert.True(first.Equals(same));
        Assert.True(first.Equals((object)same));
        Assert.False(first.Equals(null));
        Assert.False(first.Equals(new object()));
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.Contains(first.Code.ToString(), first.ToString(), StringComparison.Ordinal);
    }

    public static IEnumerable<object?[]> UnresolvedEffectTerminalCases()
    {
        (GovernedLoopEffectPhase Phase, GovernedLoopEffectOutcome Outcome, GovernedLoopEffectEvidenceStatus EvidenceStatus, string? OutcomeEvidenceId)[] postures =
        [
            (GovernedLoopEffectPhase.IntentPrepared, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Pending, null),
            (GovernedLoopEffectPhase.IntentPrepared, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Complete, null),
            (GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null),
            (GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null),
            (GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Incomplete, "outcome"),
            (GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Failed, GovernedLoopEffectEvidenceStatus.Incomplete, "outcome"),
            (GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Conflicted, GovernedLoopEffectEvidenceStatus.Conflicting, "outcome"),
            (GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null),
            (GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Conflicting, null),
            (GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Incomplete, "outcome"),
            (GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.Failed, GovernedLoopEffectEvidenceStatus.Incomplete, "outcome"),
            (GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.Conflicted, GovernedLoopEffectEvidenceStatus.Incomplete, "outcome"),
            (GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.Conflicted, GovernedLoopEffectEvidenceStatus.Conflicting, "outcome")
        ];

        foreach (var lifecycleStatus in ConclusiveTerminalStatuses())
        {
            foreach (var posture in postures)
            {
                yield return [lifecycleStatus, posture.Phase, posture.Outcome, posture.EvidenceStatus, posture.OutcomeEvidenceId];
            }
        }
    }

    public static IEnumerable<object?[]> NonCommittedProjectionTerminalCases()
    {
        (GovernedLoopProjectionClass Class, GovernedLoopProjectionStatus Status, string? ExpectedVersion)[] postures =
        [
            (GovernedLoopProjectionClass.LocalRuntime, GovernedLoopProjectionStatus.Pending, null),
            (GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Pending, "v1"),
            (GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Conflict, "v1"),
            (GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.ReconciliationRequired, "v1"),
            (GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Pending, null),
            (GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Conflict, "v1"),
            (GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.ReconciliationRequired, "v1")
        ];

        foreach (var lifecycleStatus in ConclusiveTerminalStatuses())
        {
            foreach (var posture in postures)
            {
                yield return [lifecycleStatus, posture.Class, posture.Status, posture.ExpectedVersion];
            }
        }
    }

    private static GovernedLoopRunStatus[] ConclusiveTerminalStatuses()
    {
        return [GovernedLoopRunStatus.Completed, GovernedLoopRunStatus.Failed, GovernedLoopRunStatus.Cancelled];
    }

    private static GovernedLoopFrontierStatus FrontierFor(GovernedLoopRunStatus lifecycleStatus)
    {
        return lifecycleStatus switch
        {
            GovernedLoopRunStatus.Completed => GovernedLoopFrontierStatus.Completed,
            GovernedLoopRunStatus.Failed => GovernedLoopFrontierStatus.Failed,
            GovernedLoopRunStatus.Cancelled => GovernedLoopFrontierStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(lifecycleStatus))
        };
    }
}
