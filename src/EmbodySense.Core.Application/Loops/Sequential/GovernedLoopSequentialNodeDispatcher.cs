using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Dispatches only exact builder-issued plan nodes under a guard-issued run anchor.</summary>
public sealed class GovernedLoopSequentialNodeDispatcher
{
    private readonly GovernedLoopSequentialNodeHandlerRegistry _registry;

    /// <summary>Creates an exact-descriptor dispatcher over one immutable registry snapshot.</summary>
    public GovernedLoopSequentialNodeDispatcher(GovernedLoopSequentialNodeHandlerRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
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
            || request.Attempt is < 1 or > GovernedLoopExecutionLimits.MaxNodeAttempt
            || request.Plan.SchemaVersion != 1
            || request.Node.Ordinal < 0
            || request.Node.Ordinal >= request.Plan.Nodes.Count
            || !ReferenceEquals(request.Plan.Nodes[request.Node.Ordinal], request.Node)
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
}
