using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests.Loops.Execution;

public sealed class GovernedLoopExecutionContractTests
{
    private static readonly DateTimeOffset _createdAtUtc = new(2026, 8, 10, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAtUtc = _createdAtUtc.AddMinutes(1);

    [Fact]
    public void Canonical_running_evidence_set_requires_one_exact_binding_across_all_planes()
    {
        var binding = Binding();
        var node = GovernedLoopNodeExecutionEvidence.Create("infer", ["edge-trigger-infer"], 1, GovernedLoopNodeExecutionStatus.Running, null);
        var lifecycle = GovernedLoopRunLifecycle.Create(binding, GovernedLoopRunLifecyclePayload.Create(1, 1, GovernedLoopRunStatus.Running, _createdAtUtc, _updatedAtUtc, null));
        var frontier = GovernedLoopFrontierPosture.Create(binding, GovernedLoopFrontierPayload.Create(1, 1, GovernedLoopFrontierStatus.Active, [node], _updatedAtUtc));
        var effect = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.IntentPrepared, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Pending));
        var projection = GovernedLoopProjectionPosture.Create(
            binding,
            GovernedLoopProjectionPayload.Create(1, "run-view", "project-run", GovernedLoopProjectionClass.LocalRuntime, GovernedLoopProjectionStatus.Pending, "provider-effect", "provider-effect", null, null, null, _updatedAtUtc));

        var evidence = GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, frontier, [effect], [projection]);

        Assert.True(GovernedLoopExecutionValidator.Validate(evidence).IsValid);
        Assert.Same(binding, evidence.Lifecycle.Binding);
        Assert.Same(binding, evidence.Frontier.Binding);
        Assert.Same(binding, evidence.Effects[0].Binding);
        Assert.Same(binding, evidence.Projections[0].Binding);
    }

    [Fact]
    public void Needs_review_is_terminal_only_when_ambiguity_evidence_remains_visible()
    {
        var binding = Binding();
        var lifecycle = GovernedLoopRunLifecycle.Create(binding, GovernedLoopRunLifecyclePayload.Create(1, 2, GovernedLoopRunStatus.NeedsReview, _createdAtUtc, _updatedAtUtc, _updatedAtUtc));
        var node = GovernedLoopNodeExecutionEvidence.Create("infer", ["edge-trigger-infer"], 1, GovernedLoopNodeExecutionStatus.ReviewBlocked, null);
        var frontier = GovernedLoopFrontierPosture.Create(binding, GovernedLoopFrontierPayload.Create(1, 2, GovernedLoopFrontierStatus.ReviewBlocked, [node], _updatedAtUtc));
        var ambiguous = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete));

        var validation = GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [ambiguous], []);
        var withoutAmbiguity = GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [], []);

        Assert.True(validation.IsValid);
        Assert.Contains(withoutAmbiguity.Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.ReviewEvidenceRequired);
        Assert.True(GovernedLoopExecutionStateMatrix.IsTerminal(GovernedLoopRunStatus.NeedsReview));
    }

    [Fact]
    public void Conclusive_terminal_cannot_hide_an_open_effect_and_committed_effect_cannot_redispatch()
    {
        var binding = Binding();
        var lifecycle = GovernedLoopRunLifecycle.Create(binding, GovernedLoopRunLifecyclePayload.Create(1, 2, GovernedLoopRunStatus.Completed, _createdAtUtc, _updatedAtUtc, _updatedAtUtc));
        var node = GovernedLoopNodeExecutionEvidence.Create("infer", ["edge-trigger-infer"], 1, GovernedLoopNodeExecutionStatus.Completed, "node-outcome");
        var frontier = GovernedLoopFrontierPosture.Create(binding, GovernedLoopFrontierPayload.Create(1, 2, GovernedLoopFrontierStatus.Completed, [node], _updatedAtUtc));
        var openEffect = GovernedLoopEffectPosture.Create(binding, Effect(GovernedLoopEffectPhase.IntentPrepared, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Pending));
        var committed = Effect(GovernedLoopEffectPhase.Committed, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "provider-outcome");

        var validation = GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [openEffect], []);

        Assert.Contains(validation.Errors, error => error.Code == GovernedLoopExecutionValidationErrorCode.TerminalEvidenceUnresolved);
        Assert.False(GovernedLoopExecutionStateMatrix.IsEffectDispatchEligible(committed));
        Assert.False(GovernedLoopExecutionStateMatrix.IsEffectTransitionAllowed(GovernedLoopEffectPhase.Committed, GovernedLoopEffectPhase.DispatchBoundaryReached));
    }

    private static GovernedLoopExecutionBinding Binding(long generation = 1)
    {
        var revision = GovernedLoopRevisionReference.Create(1, "graph", "revision-1", new string('a', 64));
        return GovernedLoopExecutionBinding.Create(1, "run-1", revision, generation);
    }

    private static GovernedLoopEffectPayload Effect(
        GovernedLoopEffectPhase phase,
        GovernedLoopEffectOutcome outcome,
        GovernedLoopEffectEvidenceStatus evidenceStatus,
        string? outcomeEvidenceId = null)
    {
        return GovernedLoopEffectPayload.Create(
            1,
            "provider-effect",
            "provider-operation",
            1,
            GovernedLoopEffectOrigin.Provider,
            "infer",
            new string('b', 64),
            phase,
            outcome,
            evidenceStatus,
            outcomeEvidenceId,
            null,
            _updatedAtUtc);
    }
}
