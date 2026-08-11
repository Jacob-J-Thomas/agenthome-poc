using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Dispatches only exact builder-issued plan nodes under a guard-issued run anchor.</summary>
public sealed class GovernedLoopSequentialNodeDispatcher
{
    private readonly GovernedLoopSequentialNodeHandlerRegistry _registry;
    private readonly IGovernedLoopSequentialNodeEvidenceSource _evidenceSource;

    /// <summary>Creates an exact-descriptor dispatcher over one immutable registry snapshot.</summary>
    public GovernedLoopSequentialNodeDispatcher(
        GovernedLoopSequentialNodeHandlerRegistry registry,
        IGovernedLoopSequentialNodeEvidenceSource evidenceSource)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _evidenceSource = evidenceSource ?? throw new ArgumentNullException(nameof(evidenceSource));
    }

    /// <summary>Dispatches one node only after anchor, plan, node, attempt, and live handler descriptor checks pass.</summary>
    public async Task<GovernedLoopSequentialNodeDispatchResult> DispatchAsync(
        GovernedLoopSequentialNodeDispatchRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidRequest(request))
        {
            return new GovernedLoopSequentialNodeDispatchResult(GovernedLoopSequentialNodeDispatchStatus.InvalidRequest, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_registry.TryResolve(request!.Node.Descriptor, out var handler)
            || handler is null
            || !Equals(handler.Descriptor, request.Node.Descriptor))
        {
            return new GovernedLoopSequentialNodeDispatchResult(GovernedLoopSequentialNodeDispatchStatus.UnsupportedDescriptor, null);
        }

        var result = await handler.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        if (!IsValidResult(result))
        {
            return new GovernedLoopSequentialNodeDispatchResult(GovernedLoopSequentialNodeDispatchStatus.InvalidHandlerResult, null);
        }

        var evidence = await _evidenceSource.ResolveAsync(result.EvidenceHash, CancellationToken.None).ConfigureAwait(false);
        if (!IsExactEvidence(evidence, request, result))
        {
            return new GovernedLoopSequentialNodeDispatchResult(GovernedLoopSequentialNodeDispatchStatus.InvalidHandlerResult, null);
        }

        var status = result.Status switch
        {
            GovernedLoopSequentialNodeHandlerResultStatus.Completed => GovernedLoopSequentialNodeDispatchStatus.Completed,
            GovernedLoopSequentialNodeHandlerResultStatus.Rejected => GovernedLoopSequentialNodeDispatchStatus.Rejected,
            GovernedLoopSequentialNodeHandlerResultStatus.NeedsReview => GovernedLoopSequentialNodeDispatchStatus.NeedsReview,
            _ => GovernedLoopSequentialNodeDispatchStatus.InvalidHandlerResult,
        };
        return new GovernedLoopSequentialNodeDispatchResult(status, result.EvidenceHash);
    }

    private static bool IsValidRequest(GovernedLoopSequentialNodeDispatchRequest? request)
    {
        if (request is null
            || request.SchemaVersion != GovernedLoopSequentialNodeDispatchRequest.CurrentSchemaVersion
            || request.Anchor is null
            || request.Plan is null
            || request.Node is null
            || request.Activation is null
            || request.Attempt is < 1 or > GovernedLoopExecutionLimits.MaxNodeAttempt
            || request.Plan.SchemaVersion != 1
            || request.Node.Ordinal < 0
            || request.Node.Ordinal >= request.Plan.Nodes.Count
            || !ReferenceEquals(request.Plan.Nodes[request.Node.Ordinal], request.Node)
            || request.Activation.Status != GovernedLoopNodeExecutionStatus.Running
            || request.Activation.PlanOrdinal != request.Node.Ordinal
            || !string.Equals(request.Activation.NodeId, request.Node.NodeId, StringComparison.Ordinal)
            || !Equals(request.Activation.Descriptor, request.Node.Descriptor)
            || request.Activation.Attempt != request.Attempt
            || request.Activation.AttemptOperationId is null
            || !GovernedLoopSequentialNodeDescriptors.IsSupported(request.Node.Descriptor))
        {
            return false;
        }

        var binding = request.Anchor.AdapterBinding;
        return Equals(request.Plan.Revision, binding.ExecutionBinding.Revision)
            && string.Equals(request.Plan.GraphArtifactHash, binding.GraphArtifactHash, StringComparison.Ordinal)
            && string.Equals(request.Plan.GraphLayoutHash, binding.GraphLayoutHash, StringComparison.Ordinal)
            && string.Equals(request.Anchor.InvocationSnapshot.ContentHash, binding.InvocationPayloadHash, StringComparison.Ordinal);
    }

    private static bool IsValidResult(GovernedLoopSequentialNodeHandlerResult? result)
        => result is not null
            && result.Status != GovernedLoopSequentialNodeHandlerResultStatus.Unknown
            && Enum.IsDefined(result.Status)
            && result.EvidenceHash is { Length: 64 }
            && result.EvidenceHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsExactEvidence(
        GovernedLoopSequentialNodeEvidenceReceipt? evidence,
        GovernedLoopSequentialNodeDispatchRequest request,
        GovernedLoopSequentialNodeHandlerResult result)
    {
        var binding = request.Anchor.AdapterBinding;
        var execution = binding.ExecutionBinding;
        return evidence is not null
            && evidence.SchemaVersion == GovernedLoopSequentialNodeEvidenceReceipt.CurrentSchemaVersion
            && evidence.Kind == ExpectedEvidenceKind(result.Status)
            && evidence.Disposition == result.Status
            && string.Equals(evidence.EvidenceHash, result.EvidenceHash, StringComparison.Ordinal)
            && string.Equals(evidence.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(evidence.RunId, execution.RunId, StringComparison.Ordinal)
            && Equals(evidence.Revision, execution.Revision)
            && evidence.ExecutionGeneration == execution.ExecutionGeneration
            && evidence.ActivationOrdinal == request.Activation.ActivationOrdinal
            && evidence.VisitOrdinal == request.Activation.VisitOrdinal
            && string.Equals(evidence.NodeId, request.Node.NodeId, StringComparison.Ordinal)
            && evidence.Attempt == request.Attempt
            && string.Equals(evidence.CycleId, request.Activation.CycleId, StringComparison.Ordinal)
            && evidence.CycleIteration == request.Activation.CycleIteration
            && IsExactRouteEvidence(evidence, request)
            && GovernedLoopSequentialNodeEvidenceHash.Matches(evidence);
    }

    private static bool IsExactRouteEvidence(
        GovernedLoopSequentialNodeEvidenceReceipt evidence,
        GovernedLoopSequentialNodeDispatchRequest request)
    {
        if (evidence.SelectedControlEdgeIds is null
            || evidence.SkippedControlEdgeIds is null
            || !IsSortedUnique(evidence.SelectedControlEdgeIds)
            || !IsSortedUnique(evidence.SkippedControlEdgeIds)
            || evidence.SelectedControlEdgeIds.Intersect(evidence.SkippedControlEdgeIds, StringComparer.Ordinal).Any())
        {
            return false;
        }

        if (evidence.Disposition == GovernedLoopSequentialNodeHandlerResultStatus.NeedsReview)
        {
            return evidence.ControlOutcome is null
                && evidence.SelectedControlEdgeIds.Length == 0
                && evidence.SkippedControlEdgeIds.Length == 0;
        }

        if (evidence.ControlOutcome is null or GovernedLoopControlCondition.Unknown)
        {
            return false;
        }

        var partition = evidence.SelectedControlEdgeIds.Concat(evidence.SkippedControlEdgeIds).Order(StringComparer.Ordinal);
        if (!partition.SequenceEqual(request.Activation.OutgoingControlEdgeIds, StringComparer.Ordinal))
        {
            return false;
        }

        var planEdges = request.Plan.ControlEdges.ToDictionary(edge => edge.Id, StringComparer.Ordinal);
        return evidence.SelectedControlEdgeIds.All(edgeId => planEdges.TryGetValue(edgeId, out var edge)
            && string.Equals(edge.FromNodeId, request.Node.NodeId, StringComparison.Ordinal)
            && edge.Condition == evidence.ControlOutcome)
            && evidence.SkippedControlEdgeIds.All(planEdges.ContainsKey);
    }

    private static bool IsSortedUnique(IReadOnlyList<string> values)
        => values.All(value => CustomLoopArtifactIdentifier.IsValid(value))
            && values.SequenceEqual(values.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal), StringComparer.Ordinal);

    private static GovernedLoopSequentialNodeEvidenceKind ExpectedEvidenceKind(GovernedLoopSequentialNodeHandlerResultStatus status)
        => status switch
        {
            GovernedLoopSequentialNodeHandlerResultStatus.Completed => GovernedLoopSequentialNodeEvidenceKind.CompletedOutcome,
            GovernedLoopSequentialNodeHandlerResultStatus.Rejected => GovernedLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            GovernedLoopSequentialNodeHandlerResultStatus.NeedsReview => GovernedLoopSequentialNodeEvidenceKind.AmbiguityAttention,
            _ => GovernedLoopSequentialNodeEvidenceKind.Unknown,
        };
}
