using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests.Loops.Execution;

public sealed class GovernedLoopExecutionConstructionTests
{
    [Fact]
    public void Schema_roots_reject_every_version_except_one()
    {
        var revision = GovernedLoopRevisionReference.Create(1, "graph", "revision-1", new string('a', 64));
        var binding = GovernedLoopExecutionTestFixture.Binding();

        Assert.Throws<ArgumentException>(() => GovernedLoopExecutionBinding.Create(2, "run-1", revision, 1));
        Assert.Throws<ArgumentException>(() => GovernedLoopRunLifecyclePayload.Create(2, 1, GovernedLoopRunStatus.Running, GovernedLoopExecutionTestFixture.CreatedAtUtc, GovernedLoopExecutionTestFixture.UpdatedAtUtc, null));
        Assert.Throws<ArgumentException>(() => GovernedLoopFrontierPayload.Create(2, 1, 1, GovernedLoopFrontierStatus.Active, [GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Ready)], GovernedLoopExecutionTestFixture.UpdatedAtUtc, string.Empty));
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectPayload.Create(2, "effect", "operation", 1, GovernedLoopEffectOrigin.Provider, "infer", new string('a', 64), GovernedLoopEffectPhase.IntentPrepared, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Pending, null, null, GovernedLoopExecutionTestFixture.UpdatedAtUtc));
        Assert.Throws<ArgumentException>(() => GovernedLoopProjectionPayload.Create(2, "projection", "operation", GovernedLoopProjectionClass.LocalRuntime, GovernedLoopProjectionStatus.Pending, "source", null, null, null, null, GovernedLoopExecutionTestFixture.UpdatedAtUtc));
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Running);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(binding, GovernedLoopFrontierStatus.Active);
        Assert.Throws<ArgumentException>(() => GovernedLoopExecutionEvidenceSet.Create(2, lifecycle, frontier, [], []));
    }

    [Fact]
    public void Lifecycle_requires_contiguous_time_posture_and_zero_offset_utc()
    {
        var created = GovernedLoopExecutionTestFixture.CreatedAtUtc;
        var updated = GovernedLoopExecutionTestFixture.UpdatedAtUtc;

        Assert.Throws<ArgumentException>(() => GovernedLoopRunLifecyclePayload.Create(1, 1, GovernedLoopRunStatus.Unknown, created, updated, null));
        Assert.Throws<ArgumentException>(() => GovernedLoopRunLifecyclePayload.Create(1, 1, (GovernedLoopRunStatus)99, created, updated, null));
        Assert.Throws<ArgumentException>(() => GovernedLoopRunLifecyclePayload.Create(1, 1, GovernedLoopRunStatus.Running, updated, created, null));
        Assert.Throws<ArgumentException>(() => GovernedLoopRunLifecyclePayload.Create(1, 1, GovernedLoopRunStatus.Running, default, updated, null));
        Assert.Throws<ArgumentException>(() => GovernedLoopRunLifecyclePayload.Create(1, 1, GovernedLoopRunStatus.Running, created.ToOffset(TimeSpan.FromHours(1)), updated, null));
        Assert.Throws<ArgumentException>(() => GovernedLoopRunLifecyclePayload.Create(1, 1, GovernedLoopRunStatus.Running, created, updated, updated));
        Assert.Throws<ArgumentException>(() => GovernedLoopRunLifecyclePayload.Create(1, 1, GovernedLoopRunStatus.Completed, created, updated, null));
        Assert.Throws<ArgumentException>(() => GovernedLoopRunLifecyclePayload.Create(1, 1, GovernedLoopRunStatus.Completed, created, updated, updated.AddSeconds(-1)));
        Assert.Equal(updated, GovernedLoopRunLifecyclePayload.Create(1, 1, GovernedLoopRunStatus.NeedsReview, created, updated, updated).TerminalAtUtc);
    }

    [Theory]
    [InlineData(GovernedLoopNodeExecutionStatus.Ready, null, false, null, false, true)]
    [InlineData(GovernedLoopNodeExecutionStatus.Ready, 1, true, null, false, false)]
    [InlineData(GovernedLoopNodeExecutionStatus.Skipped, null, false, null, false, false)]
    [InlineData(GovernedLoopNodeExecutionStatus.Skipped, null, false, "skip-evidence", true, true)]
    [InlineData(GovernedLoopNodeExecutionStatus.Running, 1, true, null, false, true)]
    [InlineData(GovernedLoopNodeExecutionStatus.Running, 1, true, "outcome", true, false)]
    [InlineData(GovernedLoopNodeExecutionStatus.Completed, 1, true, "outcome", true, true)]
    [InlineData(GovernedLoopNodeExecutionStatus.Completed, 1, true, null, false, false)]
    [InlineData(GovernedLoopNodeExecutionStatus.Failed, 1, true, "outcome", true, true)]
    [InlineData(GovernedLoopNodeExecutionStatus.ReviewBlocked, 1, true, null, false, true)]
    [InlineData(GovernedLoopNodeExecutionStatus.ReviewBlocked, 1, true, "outcome", true, true)]
    [InlineData(GovernedLoopNodeExecutionStatus.ReviewBlocked, 1, false, null, false, false)]
    public void Node_attempt_and_outcome_shape_is_closed(GovernedLoopNodeExecutionStatus status, int? attempt, bool hasAttemptOperation, string? outcomeEvidenceId, bool hasOutcomeEvidenceHash, bool expected)
    {
        var action = () => GovernedLoopNodeExecutionEvidence.Create(
            0,
            "infer",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "provider-inference", 1),
            ["edge"],
            [],
            status,
            attempt,
            hasAttemptOperation ? "attempt-infer-1" : null,
            outcomeEvidenceId,
            hasOutcomeEvidenceHash ? new string('f', 64) : null);

        if (expected)
        {
            Assert.NotNull(action());
        }
        else
        {
            Assert.Throws<ArgumentException>(action);
        }
    }

    [Fact]
    public void Immutable_collections_are_defensive_and_require_sorted_unique_inputs()
    {
        var edges = new[] { "edge-a", "edge-b" };
        var node = CreateNode(GovernedLoopNodeExecutionStatus.Running, incomingEdgeIds: edges);
        edges[0] = "changed";
        Assert.Equal(["edge-a", "edge-b"], node.IncomingControlEdgeIds);
        Assert.Throws<ArgumentException>(() => CreateNode(GovernedLoopNodeExecutionStatus.Running, incomingEdgeIds: ["edge-b", "edge-a"]));
        Assert.Throws<ArgumentException>(() => CreateNode(GovernedLoopNodeExecutionStatus.Running, incomingEdgeIds: ["edge-a", "edge-a"]));

        var nodes = new[]
        {
            GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Completed, "a"),
            GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Ready, "b", planOrdinal: 1)
        };
        var frontier = GovernedLoopFrontierPayload.Create(1, 1, 1, GovernedLoopFrontierStatus.Active, nodes, GovernedLoopExecutionTestFixture.UpdatedAtUtc, string.Empty);
        nodes[0] = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Completed, "changed");
        Assert.Equal("a", frontier.Nodes[0].NodeId);
        Assert.Throws<ArgumentException>(() => GovernedLoopFrontierPayload.Create(1, 1, 1, GovernedLoopFrontierStatus.Active, frontier.Nodes.Reverse(), GovernedLoopExecutionTestFixture.UpdatedAtUtc, string.Empty));
        Assert.Throws<ArgumentException>(() => GovernedLoopFrontierPayload.Create(1, 1, 1, GovernedLoopFrontierStatus.Unknown, [node], GovernedLoopExecutionTestFixture.UpdatedAtUtc, string.Empty));
        Assert.Throws<ArgumentException>(() => GovernedLoopFrontierPayload.Create(1, 1, 1, GovernedLoopFrontierStatus.Completed, [node], GovernedLoopExecutionTestFixture.UpdatedAtUtc, string.Empty));
        Assert.Equal(1, frontier.SchemaVersion);
    }

    [Fact]
    public void Frontier_factory_rejects_implicit_duplicate_visits_but_allows_multiple_ready_activations()
    {
        var completed = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Completed, "same");
        var duplicate = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Ready, "same", planOrdinal: 1);
        var readyA = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Ready, "a");
        var readyB = GovernedLoopExecutionTestFixture.Node(GovernedLoopNodeExecutionStatus.Ready, "b", planOrdinal: 1);

        Assert.Throws<ArgumentException>(() => GovernedLoopFrontierPayload.Create(1, 1, 1, GovernedLoopFrontierStatus.Active, [completed, duplicate], GovernedLoopExecutionTestFixture.UpdatedAtUtc, string.Empty));
        var multipleReady = GovernedLoopFrontierPayload.Create(1, 1, 1, GovernedLoopFrontierStatus.Active, [readyA, readyB], GovernedLoopExecutionTestFixture.UpdatedAtUtc, string.Empty);
        Assert.Equal(2, multipleReady.Nodes.Count(node => node.Status == GovernedLoopNodeExecutionStatus.Ready));
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopFrontierPayload.Create(1, 1, 2, GovernedLoopFrontierStatus.Active, [readyA], GovernedLoopExecutionTestFixture.UpdatedAtUtc, string.Empty));
    }

    [Theory]
    [InlineData(GovernedLoopEffectOrigin.Provider, false, false)]
    [InlineData(GovernedLoopEffectOrigin.Actuator, false, false)]
    [InlineData(GovernedLoopEffectOrigin.MemoryMutation, false, false)]
    [InlineData(GovernedLoopEffectOrigin.Publication, false, true)]
    [InlineData(GovernedLoopEffectOrigin.Notification, false, true)]
    [InlineData(GovernedLoopEffectOrigin.SystemJob, false, true)]
    [InlineData(GovernedLoopEffectOrigin.Provider, true, true)]
    public void Effect_origin_requires_node_attribution_for_graph_owned_work(GovernedLoopEffectOrigin origin, bool hasNodeId, bool expected)
    {
        var action = () => GovernedLoopEffectPayload.Create(
            1,
            "effect",
            "operation",
            1,
            origin,
            hasNodeId ? "node" : null,
            new string('a', 64),
            GovernedLoopEffectPhase.IntentPrepared,
            GovernedLoopEffectOutcome.None,
            GovernedLoopEffectEvidenceStatus.Pending,
            null,
            null,
            GovernedLoopExecutionTestFixture.UpdatedAtUtc);

        if (expected)
        {
            Assert.NotNull(action());
        }
        else
        {
            Assert.Throws<ArgumentException>(action);
        }
    }

    [Theory]
    [InlineData(GovernedLoopProjectionClass.LocalRuntime, GovernedLoopProjectionStatus.Pending, null, null, null, true)]
    [InlineData(GovernedLoopProjectionClass.LocalRuntime, GovernedLoopProjectionStatus.Committed, null, null, null, true)]
    [InlineData(GovernedLoopProjectionClass.LocalRuntime, GovernedLoopProjectionStatus.Conflict, "v1", null, null, false)]
    [InlineData(GovernedLoopProjectionClass.LocalRuntime, GovernedLoopProjectionStatus.Reconciled, null, null, "disposition", false)]
    [InlineData(GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Pending, "v1", null, null, true)]
    [InlineData(GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Pending, null, null, null, false)]
    [InlineData(GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Committed, "v1", "v2", null, true)]
    [InlineData(GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Committed, "v1", "v2", "disposition", false)]
    [InlineData(GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Committed, "v1", null, null, false)]
    [InlineData(GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Conflict, "v1", null, null, true)]
    [InlineData(GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Reconciled, "v1", null, "disposition", true)]
    [InlineData(GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Reconciled, "v1", "v2", "disposition", true)]
    [InlineData(GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.Reconciled, "v1", null, null, false)]
    [InlineData(GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Pending, null, null, null, true)]
    [InlineData(GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Committed, null, "etag", null, true)]
    [InlineData(GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Committed, null, "etag", "disposition", false)]
    [InlineData(GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Conflict, null, null, null, false)]
    [InlineData(GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.ReconciliationRequired, "etag", null, null, true)]
    [InlineData(GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Reconciled, "etag", null, "disposition", true)]
    [InlineData(GovernedLoopProjectionClass.Surface, GovernedLoopProjectionStatus.Reconciled, "etag", "etag-2", "disposition", true)]
    public void Projection_class_owns_version_and_reconciliation_semantics(
        GovernedLoopProjectionClass projectionClass,
        GovernedLoopProjectionStatus status,
        string? expectedVersion,
        string? committedVersion,
        string? reconciliationEvidenceId,
        bool expected)
    {
        var action = () => GovernedLoopProjectionPayload.Create(1, "projection", "operation", projectionClass, status, "source", null, expectedVersion, committedVersion, reconciliationEvidenceId, GovernedLoopExecutionTestFixture.UpdatedAtUtc);

        if (expected)
        {
            Assert.NotNull(action());
        }
        else
        {
            Assert.Throws<ArgumentException>(action);
        }
    }

    [Theory]
    [InlineData("runId")]
    [InlineData("nodeId")]
    [InlineData("edgeId")]
    [InlineData("outcomeEvidenceId")]
    [InlineData("effectId")]
    [InlineData("operationId")]
    [InlineData("originNodeId")]
    [InlineData("intentHash")]
    [InlineData("reconciliationEvidenceId")]
    [InlineData("projectionId")]
    [InlineData("sourceEvidenceId")]
    [InlineData("effectReference")]
    [InlineData("expectedVersion")]
    [InlineData("committedVersion")]
    [InlineData("projectionReconciliationEvidenceId")]
    public void Every_bounded_text_surface_rejects_malformed_utf16(string surface)
    {
        var malformed = new string('\ud800', 1);

        Assert.ThrowsAny<ArgumentException>(() => CreateWithMalformed(surface, malformed));
    }

    private static object CreateWithMalformed(string surface, string malformed)
    {
        var revision = GovernedLoopRevisionReference.Create(1, "graph", "revision-1", new string('a', 64));
        return surface switch
        {
            "runId" => GovernedLoopExecutionBinding.Create(1, malformed, revision, 1),
            "nodeId" => CreateNode(GovernedLoopNodeExecutionStatus.Running, malformed),
            "edgeId" => CreateNode(GovernedLoopNodeExecutionStatus.Running, incomingEdgeIds: [malformed]),
            "outcomeEvidenceId" => CreateNode(GovernedLoopNodeExecutionStatus.Completed, outcomeEvidenceId: malformed),
            "effectId" => CreateEffect(effectId: malformed),
            "operationId" => CreateEffect(operationId: malformed),
            "originNodeId" => CreateEffect(originNodeId: malformed),
            "intentHash" => CreateEffect(intentHash: malformed),
            "reconciliationEvidenceId" => CreateEffect(phase: GovernedLoopEffectPhase.Reconciled, outcome: GovernedLoopEffectOutcome.OutcomeUnknown, evidenceStatus: GovernedLoopEffectEvidenceStatus.Complete, reconciliationEvidenceId: malformed),
            "projectionId" => CreateProjection(projectionId: malformed),
            "sourceEvidenceId" => CreateProjection(sourceEvidenceId: malformed),
            "effectReference" => CreateProjection(effectId: malformed),
            "expectedVersion" => CreateProjection(projectionClass: GovernedLoopProjectionClass.DurableReadModel, expectedVersion: malformed),
            "committedVersion" => CreateProjection(projectionClass: GovernedLoopProjectionClass.Surface, status: GovernedLoopProjectionStatus.Committed, committedVersion: malformed),
            "projectionReconciliationEvidenceId" => CreateProjection(projectionClass: GovernedLoopProjectionClass.Surface, status: GovernedLoopProjectionStatus.Reconciled, expectedVersion: "etag", reconciliationEvidenceId: malformed),
            _ => throw new ArgumentOutOfRangeException(nameof(surface))
        };
    }

    private static GovernedLoopNodeExecutionEvidence CreateNode(
        GovernedLoopNodeExecutionStatus status,
        string nodeId = "infer",
        IEnumerable<string>? incomingEdgeIds = null,
        string? outcomeEvidenceId = null)
    {
        var hasAttempt = status is not (GovernedLoopNodeExecutionStatus.Ready or GovernedLoopNodeExecutionStatus.Skipped);
        var selectedOutcome = outcomeEvidenceId ?? (status is GovernedLoopNodeExecutionStatus.Completed or GovernedLoopNodeExecutionStatus.Failed ? "outcome" : null);
        return GovernedLoopNodeExecutionEvidence.Create(
            0,
            nodeId,
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "provider-inference", 1),
            incomingEdgeIds ?? ["edge"],
            [],
            status,
            hasAttempt ? 1 : null,
            hasAttempt ? "attempt-infer-1" : null,
            selectedOutcome,
            selectedOutcome is null ? null : new string('f', 64));
    }

    private static GovernedLoopEffectPayload CreateEffect(
        string effectId = "effect",
        string operationId = "operation",
        string? originNodeId = "node",
        string? intentHash = null,
        GovernedLoopEffectPhase phase = GovernedLoopEffectPhase.IntentPrepared,
        GovernedLoopEffectOutcome outcome = GovernedLoopEffectOutcome.None,
        GovernedLoopEffectEvidenceStatus evidenceStatus = GovernedLoopEffectEvidenceStatus.Pending,
        string? reconciliationEvidenceId = null)
    {
        return GovernedLoopEffectPayload.Create(1, effectId, operationId, 1, GovernedLoopEffectOrigin.Provider, originNodeId, intentHash ?? new string('a', 64), phase, outcome, evidenceStatus, null, reconciliationEvidenceId, GovernedLoopExecutionTestFixture.UpdatedAtUtc);
    }

    private static GovernedLoopProjectionPayload CreateProjection(
        string projectionId = "projection",
        string sourceEvidenceId = "source",
        string? effectId = null,
        GovernedLoopProjectionClass projectionClass = GovernedLoopProjectionClass.LocalRuntime,
        GovernedLoopProjectionStatus status = GovernedLoopProjectionStatus.Pending,
        string? expectedVersion = null,
        string? committedVersion = null,
        string? reconciliationEvidenceId = null)
    {
        return GovernedLoopProjectionPayload.Create(1, projectionId, "operation", projectionClass, status, sourceEvidenceId, effectId, expectedVersion, committedVersion, reconciliationEvidenceId, GovernedLoopExecutionTestFixture.UpdatedAtUtc);
    }
}
