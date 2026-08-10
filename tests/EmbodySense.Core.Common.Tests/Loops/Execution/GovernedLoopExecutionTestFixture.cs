using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests.Loops.Execution;

internal static class GovernedLoopExecutionTestFixture
{
    internal static DateTimeOffset CreatedAtUtc { get; } = new(2026, 8, 10, 1, 0, 0, TimeSpan.Zero);

    internal static DateTimeOffset UpdatedAtUtc { get; } = CreatedAtUtc.AddMinutes(1);

    internal static GovernedLoopExecutionBinding Binding(long generation = 1, string runId = "run-1")
    {
        var revision = GovernedLoopRevisionReference.Create(1, "graph", "revision-1", new string('a', 64));
        return GovernedLoopExecutionBinding.Create(1, runId, revision, generation);
    }

    internal static GovernedLoopRunLifecycle Lifecycle(GovernedLoopExecutionBinding binding, GovernedLoopRunStatus status, long version = 1, DateTimeOffset? updatedAtUtc = null)
    {
        var updated = updatedAtUtc ?? UpdatedAtUtc;
        DateTimeOffset? terminal = GovernedLoopExecutionStateMatrix.IsTerminal(status) ? updated : null;
        return GovernedLoopRunLifecycle.Create(binding, GovernedLoopRunLifecyclePayload.Create(1, version, status, CreatedAtUtc, updated, terminal));
    }

    internal static GovernedLoopNodeExecutionEvidence Node(GovernedLoopNodeExecutionStatus status, string nodeId = "infer", string? outcomeEvidenceId = null, IEnumerable<string>? incomingEdgeIds = null, int? attempt = null)
    {
        var selectedAttempt = attempt ?? (status is GovernedLoopNodeExecutionStatus.Ready or GovernedLoopNodeExecutionStatus.Skipped ? null : 1);
        var selectedOutcome = outcomeEvidenceId ?? (status is GovernedLoopNodeExecutionStatus.Completed or GovernedLoopNodeExecutionStatus.Failed ? "node-outcome" : null);
        return GovernedLoopNodeExecutionEvidence.Create(nodeId, incomingEdgeIds ?? ["edge-trigger-infer"], selectedAttempt, status, selectedOutcome);
    }

    internal static GovernedLoopFrontierPosture Frontier(GovernedLoopExecutionBinding binding, GovernedLoopFrontierStatus status, long version = 1, IEnumerable<GovernedLoopNodeExecutionEvidence>? nodes = null, DateTimeOffset? updatedAtUtc = null)
    {
        var selectedNodes = nodes ?? [Node(NodeStatusFor(status))];
        return GovernedLoopFrontierPosture.Create(binding, GovernedLoopFrontierPayload.Create(1, version, status, selectedNodes, updatedAtUtc ?? UpdatedAtUtc));
    }

    internal static GovernedLoopEffectPayload Effect(
        GovernedLoopEffectPhase phase = GovernedLoopEffectPhase.IntentPrepared,
        GovernedLoopEffectOutcome outcome = GovernedLoopEffectOutcome.None,
        GovernedLoopEffectEvidenceStatus evidenceStatus = GovernedLoopEffectEvidenceStatus.Pending,
        string effectId = "provider-effect",
        string? outcomeEvidenceId = null,
        string? reconciliationEvidenceId = null,
        DateTimeOffset? updatedAtUtc = null,
        GovernedLoopEffectOrigin origin = GovernedLoopEffectOrigin.Provider,
        string? originNodeId = "infer",
        string? operationId = null,
        long effectGeneration = 1)
    {
        return GovernedLoopEffectPayload.Create(
            1,
            effectId,
            operationId ?? effectId + "-operation",
            effectGeneration,
            origin,
            originNodeId,
            new string('b', 64),
            phase,
            outcome,
            evidenceStatus,
            outcomeEvidenceId,
            reconciliationEvidenceId,
            updatedAtUtc ?? UpdatedAtUtc);
    }

    internal static GovernedLoopProjectionPayload Projection(
        GovernedLoopProjectionClass projectionClass = GovernedLoopProjectionClass.LocalRuntime,
        GovernedLoopProjectionStatus status = GovernedLoopProjectionStatus.Pending,
        string projectionId = "run-view",
        string sourceEvidenceId = "provider-effect",
        string? effectId = "provider-effect",
        string? expectedVersion = null,
        string? committedVersion = null,
        string? reconciliationEvidenceId = null,
        DateTimeOffset? updatedAtUtc = null)
    {
        return GovernedLoopProjectionPayload.Create(
            1,
            projectionId,
            projectionId + "-operation",
            projectionClass,
            status,
            sourceEvidenceId,
            effectId,
            expectedVersion,
            committedVersion,
            reconciliationEvidenceId,
            updatedAtUtc ?? UpdatedAtUtc);
    }

    private static GovernedLoopNodeExecutionStatus NodeStatusFor(GovernedLoopFrontierStatus status)
    {
        return status switch
        {
            GovernedLoopFrontierStatus.Active => GovernedLoopNodeExecutionStatus.Running,
            GovernedLoopFrontierStatus.Waiting => GovernedLoopNodeExecutionStatus.Waiting,
            GovernedLoopFrontierStatus.ReviewBlocked => GovernedLoopNodeExecutionStatus.ReviewBlocked,
            GovernedLoopFrontierStatus.Completed => GovernedLoopNodeExecutionStatus.Completed,
            GovernedLoopFrontierStatus.Failed => GovernedLoopNodeExecutionStatus.Failed,
            GovernedLoopFrontierStatus.Cancelled => GovernedLoopNodeExecutionStatus.Ready,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
    }
}
