using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanReviewAdmissionProcessLossHost
{
    private const int ProcessLossExitCode = 175;

    internal static async Task<int> RunAsync(string workspaceRoot, string runId, string boundaryText)
    {
        if (!Enum.TryParse<CustomLoopRunPublicationBoundary>(boundaryText, out var boundary)
            || !Enum.IsDefined(boundary)
            || boundary == 0)
        {
            return 2;
        }

        var paths = new WorkspacePaths(workspaceRoot);
        using var store = new CustomLoopRunStore(paths, null, (currentBoundary, _) => ExitAfterBoundaryAsync(currentBoundary, boundary));
        var current = await store.GetAsync(runId);
        if (current?.Frontier is null || current.SequentialAdapterBinding is null)
        {
            return 3;
        }

        var atUtc = current.UpdatedAtUtc.AddMinutes(1);
        var transition = GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(current.Frontier, current.SequentialAdapterBinding, null, null, atUtc);
        if (transition.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || transition.Frontier is not GovernedLoopFrontierPosture blocked)
        {
            return 4;
        }

        var request = CreateRequest(current, blocked, "process-loss");
        var result = await new HumanReviewAdmissionService(store).AdmitAsync(new HumanReviewAdmissionCommand(current.Id, current.LifecycleVersion, request, blocked));
        return result.Status == CustomLoopRunStoreStatus.Updated ? 0 : 5;
    }

    internal static HumanReviewRequest CreateRequest(CustomLoopRunRecord predecessor, GovernedLoopFrontierPosture blocked, string identity)
    {
        var blockedNode = blocked.Payload.Nodes.Single(node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked);
        var binding = HumanReviewContractHash.ApplyBinding(new HumanReviewBinding(
            1,
            blocked.WorkspaceId,
            predecessor.Id,
            blocked.Binding.Revision.GraphId,
            blocked.Binding.Revision.RevisionId,
            blocked.Binding.Revision.ExecutableHash,
            blockedNode.NodeId,
            blockedNode.ActivationOrdinal,
            null,
            blockedNode.Attempt!.Value,
            "frontier-" + identity,
            blocked.Payload.FrontierVersion,
            blocked.Payload.ContentHash,
            Hash('a'),
            Hash('b'),
            Hash('c'),
            Hash('d'),
            Hash('e'),
            Hash('f'),
            Hash('1'),
            null,
            string.Empty));
        var scope = HumanReviewContractHash.ApplyApprovalScope(new HumanReviewApprovalScope(HumanReviewApprovalScopeKind.Continuation, binding.BindingHash, null, string.Empty));
        var timing = new HumanReviewTiming(predecessor.UpdatedAtUtc, predecessor.UpdatedAtUtc.AddMinutes(10), predecessor.UpdatedAtUtc.AddHours(1));
        return HumanReviewContractHash.ApplyRequest(new HumanReviewRequest(
            1,
            "review-request-" + identity,
            "review-request-operation-" + identity,
            binding,
            HumanReviewPurpose.Continuation,
            ImmutableArray.Create(HumanReviewDecisionKind.Approve, HumanReviewDecisionKind.Reject, HumanReviewDecisionKind.Cancel, HumanReviewDecisionKind.RequestInformation),
            ImmutableArray.Create(new HumanReviewReviewerScope("reviewer-role-one", ImmutableArray.Create("scope-alpha", "scope-beta"))),
            scope,
            ImmutableArray.Create(
                HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Action, "Action", "Redacted action.", string.Empty)),
                HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Result, "Result", "Redacted result.", string.Empty)),
                HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Evidence, "Evidence", "Redacted evidence.", string.Empty))),
            timing,
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "human-review-store", "request-correlation-" + identity, timing.CreatedAtUtc, string.Empty)),
            string.Empty));
    }

    private static ValueTask ExitAfterBoundaryAsync(CustomLoopRunPublicationBoundary currentBoundary, CustomLoopRunPublicationBoundary requestedBoundary)
    {
        if (currentBoundary == requestedBoundary)
        {
            Console.Error.WriteLine($"The test host process crashed after `{currentBoundary}`.");
            Console.Error.Flush();
            Environment.Exit(ProcessLossExitCode);
        }

        return ValueTask.CompletedTask;
    }

    private static string Hash(char character) => new(character, HumanReviewContractLimits.Sha256HexCharacters);
}
