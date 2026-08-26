using System.Collections.Immutable;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanReview;
using EmbodySense.Core.Persistence.HumanReview.Models;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanReviewContinuationPublicationProcessLossHost
{
    private const int ProcessLossExitCode = 176;

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
        var review = current?.HumanReview;
        var reservation = review?.ContinuationReservation;
        if (current is null
            || review is null
            || reservation is null
            || review.AcceptedTerminalDecision?.Kind != HumanReviewDecisionKind.Approve
            || review.Continuation is not null)
        {
            return 3;
        }

        var publishedAtUtc = current.UpdatedAtUtc.AddSeconds(1);
        var wake = HumanReviewContinuationContractHash.ApplyWake(new HumanReviewContinuationWake(
            1,
            "wake-process-loss",
            new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash),
            reservation.Decision,
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            review.Request.Binding.BindingHash,
            1,
            publishedAtUtc,
            review.Request.Timing.ExpiresAtUtc,
            Provenance("wake-process-loss", publishedAtUtc),
            string.Empty));
        var continuation = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(
            1,
            wake,
            ImmutableArray<HumanReviewContinuationClaim>.Empty,
            null,
            null,
            string.Empty));
        var result = await new HumanReviewContinuationRunStore(store).PublishAsync(current.Id, current.LifecycleVersion, continuation);
        return result.Status == HumanReviewContinuationMutationStatus.Committed ? 0 : 4;
    }

    private static HumanReviewProvenance Provenance(string correlationId, DateTimeOffset observedAtUtc)
        => HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-continuation-store", correlationId, observedAtUtc, string.Empty));

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
