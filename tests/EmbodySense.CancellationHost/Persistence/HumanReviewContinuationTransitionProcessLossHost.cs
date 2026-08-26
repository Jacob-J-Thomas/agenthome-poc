using System.Collections.Immutable;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanReview;
using EmbodySense.Core.Persistence.HumanReview.Models;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanReviewContinuationTransitionProcessLossHost
{
    private const int ProcessLossExitCode = 177;

    internal static async Task<int> RunAsync(string workspaceRoot, string runId, string transition, string boundaryText)
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
        var review = current?.HumanReview;
        var continuation = review?.Continuation;
        var reservation = review?.ContinuationReservation;
        if (current is null
            || review is null
            || continuation is null
            || reservation is null
            || review.AcceptedTerminalDecision?.Kind != HumanReviewDecisionKind.Approve)
        {
            return 3;
        }

        var continuations = new HumanReviewContinuationRunStore(store);
        var result = transition switch
        {
            "claim" when continuation.Claims.IsEmpty && continuation.Completion is null && continuation.Retirement is null
                => await continuations.ClaimAsync(current.Id, current.LifecycleVersion, Claim(continuation.Wake, reservation)),
            "completion" when continuation.Claims is [.., { } active] && continuation.Completion is null && continuation.Retirement is null
                => await continuations.CompleteAsync(current.Id, current.LifecycleVersion, Completion(review.Request, continuation.Wake, reservation, active)),
            "retirement" when continuation.Claims is [.., { } active] && continuation.Completion is null && continuation.Retirement is null
                => await continuations.RetireAsync(current.Id, current.LifecycleVersion, new HumanReviewContinuationClaimReference(active.ClaimId, active.ClaimHash), Retirement(continuation.Wake, reservation, active)),
            _ => null
        };
        return result?.Status == HumanReviewContinuationMutationStatus.Committed ? 0 : 4;
    }

    private static HumanReviewContinuationClaim Claim(HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation)
    {
        var claimedAtUtc = wake.PublishedAtUtc.AddMinutes(1);
        return HumanReviewContinuationContractHash.ApplyClaim(new HumanReviewContinuationClaim(
            1,
            "claim-process-loss",
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            "worker-claim-process-loss",
            claimedAtUtc,
            claimedAtUtc.AddMinutes(5),
            Provenance("claim-process-loss", claimedAtUtc),
            string.Empty));
    }

    private static HumanReviewContinuationCompletion Completion(HumanReviewRequest request, HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation, HumanReviewContinuationClaim claim)
    {
        var completedAtUtc = claim.ClaimedAtUtc.AddSeconds(1);
        var receipt = HumanReviewContinuationContractHash.ApplyReleaseReceipt(new HumanReviewContinuationReleaseReceipt(
            1,
            "release-completion-process-loss",
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            HumanReviewContinuationReleaseKind.Continuation,
            HumanReviewContinuationReleaseDisposition.Released,
            Hash('a'),
            Hash('b'),
            null,
            string.Empty));
        return HumanReviewContinuationContractHash.ApplyCompletion(new HumanReviewContinuationCompletion(
            1,
            "completion-process-loss",
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            receipt,
            completedAtUtc,
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            Provenance("completion-process-loss", completedAtUtc),
            string.Empty));
    }

    private static HumanReviewContinuationRetirement Retirement(HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation, HumanReviewContinuationClaim claim)
    {
        var retiredAtUtc = claim.ClaimedAtUtc.AddSeconds(1);
        return HumanReviewContinuationContractHash.ApplyRetirement(new HumanReviewContinuationRetirement(
            1,
            "retirement-process-loss",
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            HumanReviewContinuationOutcome.Blocked,
            retiredAtUtc,
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            Provenance("retirement-process-loss", retiredAtUtc),
            string.Empty));
    }

    private static HumanReviewProvenance Provenance(string correlationId, DateTimeOffset observedAtUtc)
        => HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-continuation-store", correlationId, observedAtUtc, string.Empty));

    private static string Hash(char character) => new(character, HumanReviewContractLimits.Sha256HexCharacters);

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
}
