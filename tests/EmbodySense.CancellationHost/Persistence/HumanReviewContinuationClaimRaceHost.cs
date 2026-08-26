using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanReview;
using EmbodySense.Core.Persistence.HumanReview.Models;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanReviewContinuationClaimRaceHost
{
    internal static async Task<int> RunAsync(string workspaceRoot, string runId, string identity, string readyPath, string releasePath, string resultPath)
    {
        var paths = new WorkspacePaths(workspaceRoot);
        using var source = new CustomLoopRunStore(paths);
        var current = await source.GetAsync(runId);
        var review = current?.HumanReview;
        var continuation = review?.Continuation;
        var reservation = review?.ContinuationReservation;
        if (current is null || review is null || continuation is null || reservation is null || continuation.Claims.Length != 0)
        {
            return 2;
        }

        var claimedAtUtc = continuation.Wake.PublishedAtUtc.AddMinutes(1);
        var claim = HumanReviewContinuationContractHash.ApplyClaim(new HumanReviewContinuationClaim(
            1,
            "claim-race-" + identity,
            new HumanReviewContinuationWakeReference(continuation.Wake.WakeId, continuation.Wake.WakeHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            continuation.Wake.ExpectedGeneration,
            "worker-race-" + identity,
            claimedAtUtc,
            claimedAtUtc.AddMinutes(5),
            Provenance(identity, claimedAtUtc),
            string.Empty));

        using var inner = new CustomLoopRunStore(paths);
        var result = await new HumanReviewContinuationRunStore(new HumanReviewAdmissionRaceGateStore(inner, readyPath, releasePath))
            .ClaimAsync(current.Id, current.LifecycleVersion, claim);
        await File.WriteAllTextAsync(resultPath, result.Status.ToString());
        return result.Status is HumanReviewContinuationMutationStatus.Committed or HumanReviewContinuationMutationStatus.Conflict ? 0 : 3;
    }

    private static HumanReviewProvenance Provenance(string correlationId, DateTimeOffset observedAtUtc)
        => HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-continuation-store", correlationId, observedAtUtc, string.Empty));
}
