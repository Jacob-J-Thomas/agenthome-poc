using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanReview;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanReviewDecisionActionClaimRaceHost
{
    internal static async Task<int> RunAsync(string workspaceRoot, string runId, string identity, string readyPath, string releasePath, string resultPath)
    {
        var paths = new WorkspacePaths(workspaceRoot);
        using var source = new CustomLoopRunStore(paths);
        var current = await source.GetAsync(runId);
        var action = current?.HumanReview?.DecisionActions.SingleOrDefault(item => item is not null && item.Wake is not null && item.Claims.IsEmpty && item.Completion is null && item.Retirement is null);
        if (current is null || action?.Wake is null) return 2;

        var claimedAtUtc = action.Wake.PublishedAtUtc.AddMinutes(1);
        var claim = HumanReviewDecisionActionContractHash.ApplyClaim(new(1, "claim-race-" + identity, new(action.Wake.WakeId, action.Wake.WakeHash), new(action.Reservation.ReservationId, action.Reservation.ReservationHash), action.ExpectedGeneration, "worker-race-" + identity, claimedAtUtc, claimedAtUtc.AddMinutes(5), Provenance(identity, claimedAtUtc), string.Empty));
        var candidate = new HumanReviewDecisionActionRecoveryCandidate(current.Id, current.LifecycleVersion, new(current.HumanReview!.Request.RequestId, current.HumanReview.Request.RequestHash), action.Reservation.Decision, new(action.Wake.WakeId, action.Wake.WakeHash), action.ExpectedGeneration, action.Wake.ExpiresAtUtc, new(action.Reservation.ReservationId, action.Reservation.ReservationHash), null);

        using var inner = new CustomLoopRunStore(paths);
        var result = await new HumanReviewDecisionActionRunStore(new HumanReviewAdmissionRaceGateStore(inner, readyPath, releasePath)).ClaimAsync(new(candidate, claim));
        await File.WriteAllTextAsync(resultPath, result.Status.ToString());
        return result.Status is HumanReviewDecisionActionStoreMutationStatus.Committed or HumanReviewDecisionActionStoreMutationStatus.Conflict ? 0 : 3;
    }

    private static HumanReviewProvenance Provenance(string correlationId, DateTimeOffset observedAtUtc) => HumanReviewContractHash.ApplyProvenance(new(HumanReviewProvenanceKind.Coordinator, "action-store", correlationId, observedAtUtc, string.Empty));
}
