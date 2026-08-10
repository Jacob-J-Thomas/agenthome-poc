using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests.Loops.Execution;

public sealed class GovernedLoopExecutionLimitTests
{
    [Fact]
    public void Identifier_generation_version_attempt_and_reference_bounds_reject_limit_plus_one()
    {
        var revision = GovernedLoopRevisionReference.Create(1, "graph", "revision-1", new string('a', 64));
        var maximumId = new string('a', GovernedLoopExecutionLimits.MaxIdentifierCharacters);
        var binding = GovernedLoopExecutionBinding.Create(1, maximumId, revision, GovernedLoopExecutionLimits.MaxExecutionGeneration);
        var lifecycle = GovernedLoopRunLifecyclePayload.Create(1, GovernedLoopExecutionLimits.MaxVersion, GovernedLoopRunStatus.Running, GovernedLoopExecutionTestFixture.CreatedAtUtc, GovernedLoopExecutionTestFixture.UpdatedAtUtc, null);
        var maximumReference = new string('a', GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters);
        var node = GovernedLoopNodeExecutionEvidence.Create(maximumId, [], GovernedLoopExecutionLimits.MaxNodeAttempt, GovernedLoopNodeExecutionStatus.Completed, maximumReference);

        Assert.Equal(maximumId, binding.RunId);
        Assert.Equal(1, binding.SchemaVersion);
        Assert.Equal(revision, binding.Revision);
        Assert.Equal(GovernedLoopExecutionLimits.MaxExecutionGeneration, binding.ExecutionGeneration);
        Assert.Equal(GovernedLoopExecutionLimits.MaxVersion, lifecycle.LifecycleVersion);
        Assert.Equal(maximumReference, node.OutcomeEvidenceId);
        Assert.Throws<ArgumentException>(() => GovernedLoopExecutionBinding.Create(1, maximumId + "a", revision, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopExecutionBinding.Create(1, "run", revision, GovernedLoopExecutionLimits.MaxExecutionGeneration + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopRunLifecyclePayload.Create(1, GovernedLoopExecutionLimits.MaxVersion + 1, GovernedLoopRunStatus.Running, GovernedLoopExecutionTestFixture.CreatedAtUtc, GovernedLoopExecutionTestFixture.UpdatedAtUtc, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopNodeExecutionEvidence.Create("node", [], GovernedLoopExecutionLimits.MaxNodeAttempt + 1, GovernedLoopNodeExecutionStatus.Completed, "outcome"));
        Assert.Throws<ArgumentException>(() => GovernedLoopNodeExecutionEvidence.Create("node", [], 1, GovernedLoopNodeExecutionStatus.Completed, maximumReference + "a"));
    }

    [Fact]
    public void Incoming_edge_and_frontier_node_bounds_reject_limit_plus_one()
    {
        var maximumEdges = Enumerable.Range(0, GovernedLoopExecutionLimits.MaxIncomingEdges).Select(index => $"edge-{index:D3}").ToArray();
        var node = GovernedLoopNodeExecutionEvidence.Create("join", maximumEdges, 1, GovernedLoopNodeExecutionStatus.Running, null);
        var maximumNodes = Enumerable.Range(0, GovernedLoopExecutionLimits.MaxFrontierNodes)
            .Select(index => GovernedLoopNodeExecutionEvidence.Create($"node-{index:D3}", [], null, GovernedLoopNodeExecutionStatus.Ready, null))
            .ToArray();
        var frontier = GovernedLoopFrontierPayload.Create(1, 1, GovernedLoopFrontierStatus.Active, maximumNodes, GovernedLoopExecutionTestFixture.UpdatedAtUtc);

        Assert.Equal(GovernedLoopExecutionLimits.MaxIncomingEdges, node.IncomingEdgeIds.Count);
        Assert.Equal(GovernedLoopExecutionLimits.MaxFrontierNodes, frontier.Nodes.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopNodeExecutionEvidence.Create("join", [.. maximumEdges, "edge-512"], 1, GovernedLoopNodeExecutionStatus.Running, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopFrontierPayload.Create(1, 1, GovernedLoopFrontierStatus.Active, [.. maximumNodes, GovernedLoopNodeExecutionEvidence.Create("node-128", [], null, GovernedLoopNodeExecutionStatus.Ready, null)], GovernedLoopExecutionTestFixture.UpdatedAtUtc));
        Assert.False(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.Active, [.. maximumNodes, GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Ready, "node-128")]));
        Assert.False(GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(GovernedLoopFrontierStatus.Cancelled, new GovernedLoopNodeExecutionEvidence[] { null! }));
    }

    [Fact]
    public void Aggregate_effect_and_projection_bounds_reject_limit_plus_one_without_unbounded_enumeration()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Running);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active);
        var effect = GovernedLoopEffectPosture.Create(binding, GovernedLoopExecutionTestFixture.Effect());
        var projection = GovernedLoopProjectionPosture.Create(binding, GovernedLoopExecutionTestFixture.Projection());

        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, frontier, Enumerable.Repeat(effect, GovernedLoopExecutionLimits.MaxEffects + 1), []));
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, frontier, [], Enumerable.Repeat(projection, GovernedLoopExecutionLimits.MaxProjections + 1)));

        var validation = GovernedLoopExecutionValidator.ValidateComposition(
            1,
            lifecycle,
            frontier,
            Enumerable.Repeat(effect, GovernedLoopExecutionLimits.MaxEffects + 1).ToArray(),
            Enumerable.Repeat(projection, GovernedLoopExecutionLimits.MaxProjections + 1).ToArray());
        Assert.Equal(2, validation.Errors.Count(error => error.Code == GovernedLoopExecutionValidationErrorCode.CollectionTooLarge));
    }

    [Fact]
    public void Hash_timestamp_and_zero_bounds_fail_closed()
    {
        var revision = GovernedLoopRevisionReference.Create(1, "graph", "revision-1", new string('a', 64));

        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopExecutionBinding.Create(1, "run", revision, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopRunLifecyclePayload.Create(1, 0, GovernedLoopRunStatus.Running, GovernedLoopExecutionTestFixture.CreatedAtUtc, GovernedLoopExecutionTestFixture.UpdatedAtUtc, null));
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectPayload.Create(1, "effect", "operation", 1, GovernedLoopEffectOrigin.Provider, "node", new string('a', 63), GovernedLoopEffectPhase.IntentPrepared, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Pending, null, null, GovernedLoopExecutionTestFixture.UpdatedAtUtc));
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectPayload.Create(1, "effect", "operation", 1, GovernedLoopEffectOrigin.Provider, "node", new string('A', 64), GovernedLoopEffectPhase.IntentPrepared, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Pending, null, null, GovernedLoopExecutionTestFixture.UpdatedAtUtc));
        Assert.Throws<ArgumentException>(() => GovernedLoopProjectionPayload.Create(1, "projection", "operation", GovernedLoopProjectionClass.LocalRuntime, GovernedLoopProjectionStatus.Pending, "source", null, null, null, null, default));
    }
}
